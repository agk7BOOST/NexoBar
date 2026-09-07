using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class DeliveryQuantityService(
    OrderOperationsDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOrderOperationsCapabilityStabilizer capabilityStabilizer,
    TimeProvider timeProvider,
    ILogger<DeliveryQuantityService> logger)
{
    private const long DeliveryCommandLockNamespace = 0x44454C56434D4400;

    internal async Task<DeliveryQuantityResult> DeliverAsync(
        Guid idempotencyKey,
        Guid incorporationId,
        int contentOrdinal,
        int quantity,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var lockKey = CreateTransactionLockKey(idempotencyKey);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);

        var stabilizedSession = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (stabilizedSession is null)
        {
            return DeliveryQuantityResult.Unauthenticated();
        }

        var existingCommand = await dbContext.DeliveryCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existingCommand.Matches(
                    stabilizedSession.IdentityId,
                    incorporationId,
                    contentOrdinal,
                    quantity)
                ? DeliveryQuantityResult.Succeeded(existingCommand.ToResult())
                : DeliveryQuantityResult.IdempotencyConflict();
        }

        var dbTransaction = transaction.GetDbTransaction();
        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(
                stabilizedSession.IdentityId,
                dbTransaction,
                cancellationToken))
        {
            return DeliveryQuantityResult.Forbidden();
        }


        var orderId = await dbContext.Incorporations
            .AsNoTracking()
            .Where(incorporation => incorporation.Id == incorporationId)
            .Select(incorporation => (Guid?)incorporation.OrderId)
            .SingleOrDefaultAsync(cancellationToken);
        if (orderId is null)
        {
            return DeliveryQuantityResult.ContentNotFound();
        }

        await dbContext.Orders
            .FromSqlInterpolated(
                $"SELECT id, context FROM order_operations.orders WHERE id = {orderId.Value} FOR UPDATE")
            .AsNoTracking()
            .AnyAsync(cancellationToken);
        if (await dbContext.Liquidations.AsNoTracking().AnyAsync(
                liquidation => liquidation.OrderId == orderId.Value,
                cancellationToken))
        {
            return DeliveryQuantityResult.OrderFrozen();
        }

        var content = await dbContext.IncorporationContents
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM order_operations.incorporation_contents
                WHERE incorporation_id = {incorporationId}
                  AND content_ordinal = {contentOrdinal}
                FOR SHARE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (content is null)
        {
            return DeliveryQuantityResult.ContentNotFound();
        }

        PreparationWork? work;
        if (content.RequiresPreparationAtConfirmation)
        {
            work = await dbContext.PreparationWork
                .FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM order_operations.preparation_work
                    WHERE incorporation_id = {incorporationId}
                      AND content_ordinal = {contentOrdinal}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken);
            if (work is null)
            {
                LogInconsistent(incorporationId, contentOrdinal);
                return DeliveryQuantityResult.StateInconsistent();
            }
        }
        else
        {
            work = await dbContext.PreparationWork
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.IncorporationId == incorporationId &&
                        candidate.ContentOrdinal == contentOrdinal,
                    cancellationToken);
            if (work is not null)
            {
                LogInconsistent(incorporationId, contentOrdinal);
                return DeliveryQuantityResult.StateInconsistent();
            }
        }

        var deliveryState = await dbContext.DeliveryStates
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM order_operations.delivery_states
                WHERE incorporation_id = {incorporationId}
                  AND content_ordinal = {contentOrdinal}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        var quantityState = await dbContext.ContentQuantityStates
            .FromSqlInterpolated(
                $"SELECT * FROM order_operations.content_quantity_states WHERE incorporation_id = {incorporationId} AND content_ordinal = {contentOrdinal} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var effectiveQuantity = quantityState is null
            ? -1
            : checked(content.Quantity - quantityState.RemovedByCorrectionQuantity - quantityState.CancelledQuantity);
        if (content.Quantity <= 0 ||
            quantityState is null ||
            quantityState.CancelledQuantity < 0 ||
            (long)quantityState.RemovedByCorrectionQuantity + quantityState.CancelledQuantity > content.Quantity ||
            quantityState.RemovedByCorrectionQuantity < 0 ||
            quantityState.RemovedByCorrectionQuantity > content.Quantity ||
            effectiveQuantity < 0 ||
            IsPreparationWorkInconsistent(work, effectiveQuantity) ||
            deliveryState is null ||
            deliveryState.DeliveredQuantity < 0 ||
            deliveryState.DeliveredQuantity > effectiveQuantity ||
            (work is not null && deliveryState.DeliveredQuantity > work.ReadyQuantity))
        {
            LogInconsistent(incorporationId, contentOrdinal);
            return DeliveryQuantityResult.StateInconsistent();
        }

        var maximumQuantity = work?.ReadyQuantity ?? effectiveQuantity;
        var deliverableQuantity = maximumQuantity - deliveryState.DeliveredQuantity;
        var transition = deliveryState.Deliver(quantity, deliverableQuantity);
        if (transition == DeliveryTransition.QuantityInvalid)
        {
            return DeliveryQuantityResult.QuantityInvalid();
        }

        if (transition == DeliveryTransition.DeliverableQuantityInsufficient)
        {
            return DeliveryQuantityResult.DeliverableQuantityInsufficient();
        }

        var utcNow = timeProvider.GetUtcNow();
        var occurredAt = new DateTimeOffset(
            utcNow.Ticks - (utcNow.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);
        var historyId = Guid.CreateVersion7(occurredAt);
        var result = new DeliveryCommandResult(
            incorporationId,
            contentOrdinal,
            historyId,
            occurredAt,
            deliveryState.DeliveredQuantity);
        dbContext.DeliveryHistory.Add(new DeliveryHistory(
            historyId,
            incorporationId,
            contentOrdinal,
            quantity,
            stabilizedSession.IdentityId,
            occurredAt,
            deliveryState.DeliveredQuantity));
        dbContext.DeliveryCommands.Add(new DeliveryCommand(
            idempotencyKey,
            stabilizedSession.IdentityId,
            incorporationId,
            contentOrdinal,
            quantity,
            result));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return DeliveryQuantityResult.Succeeded(result);
    }

    private static bool IsPreparationWorkInconsistent(
        PreparationWork? work,
        int contentQuantity) =>
        work is not null &&
        (work.TotalQuantity != contentQuantity ||
         work.TotalQuantity < 0 ||
         work.PendingQuantity < 0 ||
         work.InPreparationQuantity < 0 ||
         work.ReadyQuantity < 0 ||
         (long)work.PendingQuantity +
         work.InPreparationQuantity +
         work.ReadyQuantity != work.TotalQuantity);

    private void LogInconsistent(Guid incorporationId, int contentOrdinal) =>
        logger.LogError(
            "Delivery state is inconsistent for Content {IncorporationId}/{ContentOrdinal}.",
            incorporationId,
            contentOrdinal);

    private static long CreateTransactionLockKey(Guid idempotencyKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);
        var keyPart = BinaryPrimitives.ReadInt64BigEndian(bytes[..8]);
        return keyPart ^ DeliveryCommandLockNamespace;
    }
}

internal sealed record DeliveryQuantityResult(
    DeliveryQuantityOutcome Outcome,
    DeliveryCommandResult? Response)
{
    internal static DeliveryQuantityResult Succeeded(DeliveryCommandResult response) =>
        new(DeliveryQuantityOutcome.Succeeded, response);

    internal static DeliveryQuantityResult Unauthenticated() =>
        new(DeliveryQuantityOutcome.Unauthenticated, null);

    internal static DeliveryQuantityResult Forbidden() =>
        new(DeliveryQuantityOutcome.Forbidden, null);

    internal static DeliveryQuantityResult ContentNotFound() =>
        new(DeliveryQuantityOutcome.ContentNotFound, null);

    internal static DeliveryQuantityResult QuantityInvalid() =>
        new(DeliveryQuantityOutcome.QuantityInvalid, null);

    internal static DeliveryQuantityResult DeliverableQuantityInsufficient() =>
        new(DeliveryQuantityOutcome.DeliverableQuantityInsufficient, null);

    internal static DeliveryQuantityResult IdempotencyConflict() =>
        new(DeliveryQuantityOutcome.IdempotencyConflict, null);

    internal static DeliveryQuantityResult StateInconsistent() =>
        new(DeliveryQuantityOutcome.StateInconsistent, null);

    internal static DeliveryQuantityResult OrderFrozen() =>
        new(DeliveryQuantityOutcome.OrderFrozen, null);
}

internal enum DeliveryQuantityOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden,
    ContentNotFound,
    QuantityInvalid,
    DeliverableQuantityInsufficient,
    IdempotencyConflict,
    StateInconsistent,
    OrderFrozen
}
