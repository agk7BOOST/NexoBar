using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class ContentCorrectionService(
    OrderOperationsDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOrderOperationsCapabilityStabilizer capabilityStabilizer,
    TimeProvider timeProvider)
{
    internal async Task<ContentCorrectionResult> CorrectAsync(
        Guid key, Guid orderId, Guid incorporationId, int contentOrdinal, int quantity,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var lockKey = CreateLockKey(key);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);
        var session = await sessionStabilizer.StabilizeAsync(transaction.GetDbTransaction(), cancellationToken);
        if (session is null) return new(ContentCorrectionOutcome.Unauthenticated);

        var command = await dbContext.ContentCorrectionCommands.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
        if (command is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return command.Matches(session.IdentityId, orderId, incorporationId, contentOrdinal, quantity)
                ? new(ContentCorrectionOutcome.Succeeded, command.ToResponse())
                : new(ContentCorrectionOutcome.IdempotencyConflict);
        }
        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(session.IdentityId, transaction.GetDbTransaction(), cancellationToken))
            return new(ContentCorrectionOutcome.Forbidden);

        if (!await dbContext.Orders.FromSqlInterpolated(
                $"SELECT id, context FROM order_operations.orders WHERE id = {orderId} FOR UPDATE")
                .AsNoTracking().AnyAsync(cancellationToken))
            return new(ContentCorrectionOutcome.ContentNotFound);
        if (await dbContext.Liquidations.AsNoTracking().AnyAsync(x => x.OrderId == orderId, cancellationToken))
            return new(ContentCorrectionOutcome.OrderFrozen);
        if (!await dbContext.Incorporations.AsNoTracking().AnyAsync(x => x.Id == incorporationId && x.OrderId == orderId, cancellationToken))
            return new(ContentCorrectionOutcome.ContentNotFound);

        var content = await dbContext.IncorporationContents.FromSqlInterpolated(
            $"SELECT * FROM order_operations.incorporation_contents WHERE incorporation_id = {incorporationId} AND content_ordinal = {contentOrdinal} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (content is null) return new(ContentCorrectionOutcome.ContentNotFound);
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
            : checked(content.Quantity - quantityState.RemovedByCorrectionQuantity);
        if (content.Quantity <= 0 || quantityState is null ||
            quantityState.RemovedByCorrectionQuantity < 0 || quantityState.RemovedByCorrectionQuantity > content.Quantity ||
            effectiveQuantity < 0 || content.RequiresPreparationAtConfirmation != (work is not null) ||
            delivery is null || delivery.DeliveredQuantity < 0 || delivery.DeliveredQuantity > effectiveQuantity ||
            (work is not null && (work.TotalQuantity != effectiveQuantity || work.PendingQuantity < 0 ||
                work.InPreparationQuantity < 0 || work.ReadyQuantity < 0 ||
                (long)work.PendingQuantity + work.InPreparationQuantity + work.ReadyQuantity != work.TotalQuantity ||
                delivery.DeliveredQuantity > work.ReadyQuantity)))
            return new(ContentCorrectionOutcome.StateInconsistent);

        if (quantity <= 0) return new(ContentCorrectionOutcome.QuantityInvalid);
        var eligible = work?.PendingQuantity ?? effectiveQuantity!.Value - delivery.DeliveredQuantity;
        if (quantity > eligible) return new(ContentCorrectionOutcome.QuantityExceedsEligible);
        var previousRemoved = quantityState.RemovedByCorrectionQuantity;
        quantityState.Correct(quantity, content.Quantity, eligible);
        work?.CorrectPending(quantity);

        var now = timeProvider.GetUtcNow();
        var occurredAt = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        var response = new ContentCorrectionResponse(orderId, incorporationId, contentOrdinal, Guid.CreateVersion7(occurredAt),
            quantity, content.Quantity, previousRemoved, quantityState.RemovedByCorrectionQuantity,
            effectiveQuantity!.Value, content.Quantity - quantityState.RemovedByCorrectionQuantity, occurredAt);
        dbContext.ContentCorrectionHistory.Add(new ContentCorrectionHistory(response, session.IdentityId));
        dbContext.ContentCorrectionCommands.Add(new ContentCorrectionCommand(key, session.IdentityId, response));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ContentCorrectionOutcome.Succeeded, response);
    }

    private static long CreateLockKey(Guid key)
    {
        Span<byte> bytes = stackalloc byte[16];
        key.TryWriteBytes(bytes, bigEndian: true, out _);
        return 0x43434F5252434D44 ^ BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^ BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}

internal sealed record ContentCorrectionResult(ContentCorrectionOutcome Outcome, ContentCorrectionResponse? Response = null);
internal enum ContentCorrectionOutcome
{
    Succeeded, Unauthenticated, Forbidden, ContentNotFound, QuantityInvalid,
    QuantityExceedsEligible, OrderFrozen, IdempotencyConflict, StateInconsistent
}
