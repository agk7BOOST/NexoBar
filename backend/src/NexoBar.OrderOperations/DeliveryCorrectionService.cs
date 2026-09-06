using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class DeliveryCorrectionService(
    OrderOperationsDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOrderOperationsCapabilityStabilizer capabilityStabilizer,
    TimeProvider timeProvider)
{
    internal async Task<DeliveryCorrectionResult> CorrectAsync(
        Guid key, Guid orderId, Guid incorporationId, int contentOrdinal, int quantity,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var lockKey = CreateLockKey(key);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);
        var session = await sessionStabilizer.StabilizeAsync(transaction.GetDbTransaction(), cancellationToken);
        if (session is null) return new(DeliveryCorrectionOutcome.Unauthenticated);

        var command = await dbContext.DeliveryCorrectionCommands.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
        if (command is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return command.Matches(session.IdentityId, orderId, incorporationId, contentOrdinal, quantity)
                ? new(DeliveryCorrectionOutcome.Succeeded, command.ToResponse())
                : new(DeliveryCorrectionOutcome.IdempotencyConflict);
        }
        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(session.IdentityId, transaction.GetDbTransaction(), cancellationToken))
            return new(DeliveryCorrectionOutcome.Forbidden);

        if (!await dbContext.Orders.FromSqlInterpolated(
                $"SELECT id, context FROM order_operations.orders WHERE id = {orderId} FOR UPDATE")
                .AsNoTracking().AnyAsync(cancellationToken))
            return new(DeliveryCorrectionOutcome.ContentNotFound);
        if (await dbContext.Liquidations.AsNoTracking().AnyAsync(x => x.OrderId == orderId, cancellationToken))
            return new(DeliveryCorrectionOutcome.OrderFrozen);
        if (!await dbContext.Incorporations.AsNoTracking().AnyAsync(x => x.Id == incorporationId && x.OrderId == orderId, cancellationToken))
            return new(DeliveryCorrectionOutcome.ContentNotFound);

        var content = await dbContext.IncorporationContents.FromSqlInterpolated(
            $"SELECT * FROM order_operations.incorporation_contents WHERE incorporation_id = {incorporationId} AND content_ordinal = {contentOrdinal} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (content is null) return new(DeliveryCorrectionOutcome.ContentNotFound);
        var work = await dbContext.PreparationWork.FromSqlInterpolated(
            $"SELECT * FROM order_operations.preparation_work WHERE incorporation_id = {incorporationId} AND content_ordinal = {contentOrdinal} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var delivery = await dbContext.DeliveryStates.FromSqlInterpolated(
            $"SELECT * FROM order_operations.delivery_states WHERE incorporation_id = {incorporationId} AND content_ordinal = {contentOrdinal} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var quantityState = await dbContext.ContentQuantityStates.FromSqlInterpolated(
            $"SELECT * FROM order_operations.content_quantity_states WHERE incorporation_id = {incorporationId} AND content_ordinal = {contentOrdinal} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var effectiveQuantity = quantityState is null
            ? (int?)null
            : checked(content.Quantity - quantityState.RemovedByCorrectionQuantity);
        if (content.Quantity <= 0 || quantityState is null ||
            quantityState.RemovedByCorrectionQuantity < 0 || quantityState.RemovedByCorrectionQuantity > content.Quantity ||
            effectiveQuantity <= 0 || content.RequiresPreparationAtConfirmation != (work is not null) ||
            delivery is null || delivery.DeliveredQuantity < 0 || delivery.DeliveredQuantity > effectiveQuantity ||
            (work is not null && (work.TotalQuantity != effectiveQuantity || work.PendingQuantity < 0 ||
                work.InPreparationQuantity < 0 || work.ReadyQuantity < 0 ||
                (long)work.PendingQuantity + work.InPreparationQuantity + work.ReadyQuantity != work.TotalQuantity ||
                delivery.DeliveredQuantity > work.ReadyQuantity)))
            return new(DeliveryCorrectionOutcome.StateInconsistent);

        var previous = delivery.DeliveredQuantity;
        var transition = delivery.Correct(quantity);
        if (transition != DeliveryCorrectionTransition.Corrected)
            return new(transition switch
            {
                DeliveryCorrectionTransition.QuantityInvalid => DeliveryCorrectionOutcome.QuantityInvalid,
                DeliveryCorrectionTransition.NoEffectiveDelivery => DeliveryCorrectionOutcome.NoEffectiveDelivery,
                DeliveryCorrectionTransition.QuantityExceedsDelivered => DeliveryCorrectionOutcome.QuantityExceedsDelivered,
                _ => throw new InvalidOperationException("Unknown Delivery Correction transition.")
            });

        var now = timeProvider.GetUtcNow();
        var occurredAt = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        var response = new DeliveryCorrectionResponse(orderId, incorporationId, contentOrdinal, Guid.CreateVersion7(occurredAt),
            quantity, previous, delivery.DeliveredQuantity, occurredAt);
        dbContext.DeliveryCorrectionHistory.Add(new DeliveryCorrectionHistory(response, session.IdentityId));
        dbContext.DeliveryCorrectionCommands.Add(new DeliveryCorrectionCommand(key, session.IdentityId, response));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(DeliveryCorrectionOutcome.Succeeded, response);
    }

    private static long CreateLockKey(Guid key)
    {
        Span<byte> bytes = stackalloc byte[16];
        key.TryWriteBytes(bytes, bigEndian: true, out _);
        return 0x44434F5252434D44 ^ BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^ BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}

internal sealed record DeliveryCorrectionResult(DeliveryCorrectionOutcome Outcome, DeliveryCorrectionResponse? Response = null);
internal enum DeliveryCorrectionOutcome
{
    Succeeded, Unauthenticated, Forbidden, ContentNotFound, QuantityInvalid,
    NoEffectiveDelivery, QuantityExceedsDelivered, OrderFrozen, IdempotencyConflict, StateInconsistent
}
