using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class ContentCancellationService(
    OrderOperationsDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOrderOperationsCapabilityStabilizer capabilityStabilizer,
    TimeProvider timeProvider)
{
    internal async Task<ContentCancellationResult> CancelAsync(
        Guid key, Guid orderId, Guid incorporationId, int contentOrdinal, int quantity,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var lockKey = CreateLockKey(key);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);
        var session = await sessionStabilizer.StabilizeAsync(transaction.GetDbTransaction(), cancellationToken);
        if (session is null) return new(ContentCancellationOutcome.Unauthenticated);

        var command = await dbContext.ContentCancellationCommands.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
        if (command is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return command.Matches(session.IdentityId, orderId, incorporationId, contentOrdinal, quantity)
                ? new(ContentCancellationOutcome.Succeeded, command.ToResponse())
                : new(ContentCancellationOutcome.IdempotencyConflict);
        }
        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(session.IdentityId, transaction.GetDbTransaction(), cancellationToken))
            return new(ContentCancellationOutcome.Forbidden);

        if (!await dbContext.Orders.FromSqlInterpolated(
                $"SELECT id, context FROM order_operations.orders WHERE id = {orderId} FOR UPDATE")
                .AsNoTracking().AnyAsync(cancellationToken))
            return new(ContentCancellationOutcome.ContentNotFound);
        if (await dbContext.Liquidations.AsNoTracking().AnyAsync(x => x.OrderId == orderId, cancellationToken))
            return new(ContentCancellationOutcome.OrderFrozen);
        if (!await dbContext.Incorporations.AsNoTracking().AnyAsync(x => x.Id == incorporationId && x.OrderId == orderId, cancellationToken))
            return new(ContentCancellationOutcome.ContentNotFound);

        var content = await dbContext.IncorporationContents.FromSqlInterpolated(
            $"SELECT * FROM order_operations.incorporation_contents WHERE incorporation_id = {incorporationId} AND content_ordinal = {contentOrdinal} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (content is null) return new(ContentCancellationOutcome.ContentNotFound);
        var work = await dbContext.PreparationWork.FromSqlInterpolated(
            $"SELECT * FROM order_operations.preparation_work WHERE incorporation_id = {incorporationId} AND content_ordinal = {contentOrdinal} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var delivery = await dbContext.DeliveryStates.FromSqlInterpolated(
            $"SELECT * FROM order_operations.delivery_states WHERE incorporation_id = {incorporationId} AND content_ordinal = {contentOrdinal} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var quantityState = await dbContext.ContentQuantityStates.FromSqlInterpolated(
            $"SELECT * FROM order_operations.content_quantity_states WHERE incorporation_id = {incorporationId} AND content_ordinal = {contentOrdinal} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var effectiveQuantity = quantityState is null
            ? (int?)null
            : checked(content.Quantity - quantityState.RemovedByCorrectionQuantity - quantityState.CancelledQuantity);
        if (content.Quantity <= 0 || quantityState is null ||
            quantityState.RemovedByCorrectionQuantity < 0 || quantityState.CancelledQuantity < 0 ||
            (long)quantityState.RemovedByCorrectionQuantity + quantityState.CancelledQuantity > content.Quantity ||
            effectiveQuantity < 0 || content.RequiresPreparationAtConfirmation != (work is not null) ||
            delivery is null || delivery.DeliveredQuantity < 0 || delivery.DeliveredQuantity > effectiveQuantity ||
            (work is not null && (work.TotalQuantity != effectiveQuantity || work.PendingQuantity < 0 ||
                work.InPreparationQuantity < 0 || work.ReadyQuantity < 0 ||
                (long)work.PendingQuantity + work.InPreparationQuantity + work.ReadyQuantity != work.TotalQuantity ||
                delivery.DeliveredQuantity > work.ReadyQuantity)))
            return new(ContentCancellationOutcome.StateInconsistent);

        if (quantity <= 0) return new(ContentCancellationOutcome.QuantityInvalid);
        var eligible = work?.PendingQuantity ?? effectiveQuantity!.Value - delivery.DeliveredQuantity;
        if (quantity > eligible) return new(ContentCancellationOutcome.QuantityExceedsEligible);
        var previousCancelled = quantityState.CancelledQuantity;
        quantityState.Cancel(quantity, content.Quantity, eligible);
        work?.CancelPending(quantity);

        var now = timeProvider.GetUtcNow();
        var occurredAt = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        var response = new ContentCancellationResponse(orderId, incorporationId, contentOrdinal, Guid.CreateVersion7(occurredAt),
            quantity, content.Quantity, previousCancelled, quantityState.CancelledQuantity,
            effectiveQuantity!.Value, content.Quantity - quantityState.RemovedByCorrectionQuantity - quantityState.CancelledQuantity, occurredAt);
        dbContext.ContentCancellationHistory.Add(new ContentCancellationHistory(response, session.IdentityId));
        dbContext.ContentCancellationCommands.Add(new ContentCancellationCommand(key, session.IdentityId, response));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ContentCancellationOutcome.Succeeded, response);
    }

    private static long CreateLockKey(Guid key)
    {
        Span<byte> bytes = stackalloc byte[16];
        key.TryWriteBytes(bytes, bigEndian: true, out _);
        return 0x4343414E434D4421 ^ BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^ BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}

internal sealed record ContentCancellationResult(ContentCancellationOutcome Outcome, ContentCancellationResponse? Response = null);
internal enum ContentCancellationOutcome
{
    Succeeded, Unauthenticated, Forbidden, ContentNotFound, QuantityInvalid,
    QuantityExceedsEligible, OrderFrozen, IdempotencyConflict, StateInconsistent
}
