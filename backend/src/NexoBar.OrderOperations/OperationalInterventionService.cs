using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class OperationalInterventionService(
    OrderOperationsDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOperationalInterventionCapabilityStabilizer capabilityStabilizer,
    TimeProvider timeProvider,
    IPreparationDestinationInvalidationPublisher invalidations,
    IOrderInvalidationPublisher orderInvalidations)
{
    internal Task<PreparationProgressResult> InterveneInPreparationAsync(Guid key, Guid workId, int quantity, CancellationToken token) =>
        ExecuteAsync(false, key, workId, quantity, token);

    internal Task<PreparationProgressResult> InterveneReadyAsync(Guid key, Guid workId, int quantity, CancellationToken token) =>
        ExecuteAsync(true, key, workId, quantity, token);

    private async Task<PreparationProgressResult> ExecuteAsync(bool ready, Guid key, Guid workId, int quantity, CancellationToken token)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        // Intervention shares the durable Work-command namespace, including its advisory lock.
        var lockKey = PreparationProgressService.CreateTransactionLockKey(key);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", token);
        var session = await sessionStabilizer.StabilizeAsync(transaction.GetDbTransaction(), token);
        if (session is null) return PreparationProgressResult.Unauthenticated();
        var command = await dbContext.PreparationCommands.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, token);
        if (command is not null)
        {
            await transaction.CommitAsync(token);
            return command.Matches(session.IdentityId, ready ? PreparationCommand.InterveneReadyCommandKind : PreparationCommand.InterveneInPreparationCommandKind, workId, quantity)
                ? PreparationProgressResult.Succeeded(command.ToResult())
                : PreparationProgressResult.IdempotencyConflict();
        }
        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(session.IdentityId, transaction.GetDbTransaction(), token))
            return PreparationProgressResult.Forbidden();

        var target = await (from work in dbContext.PreparationWork.AsNoTracking()
            join incorporation in dbContext.Incorporations.AsNoTracking() on work.IncorporationId equals incorporation.Id
            where work.Id == workId
            select new { incorporation.OrderId, work.IncorporationId, work.ContentOrdinal }).SingleOrDefaultAsync(token);
        if (target is null) return PreparationProgressResult.WorkNotFound();
        if (!await dbContext.Orders.FromSqlInterpolated(
                $"SELECT id, context FROM order_operations.orders WHERE id = {target.OrderId} FOR UPDATE")
                .AsNoTracking().AnyAsync(token)) return PreparationProgressResult.StateInconsistent();
        if (await dbContext.OrderCancellationStates.AsNoTracking().AnyAsync(x => x.OrderId == target.OrderId, token))
            return PreparationProgressResult.OrderCancelled();

        if (await dbContext.Liquidations.AsNoTracking().AnyAsync(x => x.OrderId == target.OrderId, token))
            return PreparationProgressResult.OrderFrozen();

        // Same-Order coordination precedes the stable Content -> Work -> Delivery -> quantities order.
        var content = await dbContext.IncorporationContents.FromSqlInterpolated(
            $"SELECT * FROM order_operations.incorporation_contents WHERE incorporation_id = {target.IncorporationId} AND content_ordinal = {target.ContentOrdinal} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(token);
        var workState = await dbContext.PreparationWork.FromSqlInterpolated(
            $"SELECT * FROM order_operations.preparation_work WHERE incorporation_id = {target.IncorporationId} AND content_ordinal = {target.ContentOrdinal} FOR UPDATE")
            .SingleOrDefaultAsync(token);
        var delivery = await dbContext.DeliveryStates.FromSqlInterpolated(
            $"SELECT * FROM order_operations.delivery_states WHERE incorporation_id = {target.IncorporationId} AND content_ordinal = {target.ContentOrdinal} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(token);
        var quantities = await dbContext.ContentQuantityStates.FromSqlInterpolated(
            $"SELECT * FROM order_operations.content_quantity_states WHERE incorporation_id = {target.IncorporationId} AND content_ordinal = {target.ContentOrdinal} FOR UPDATE")
            .SingleOrDefaultAsync(token);
        if (content is null || workState is null || workState.Id != workId ||
            !OperationalInterventionState.IsCoherent(content, workState, delivery, quantities))
            return PreparationProgressResult.StateInconsistent();
        if (quantity <= 0) return PreparationProgressResult.QuantityInvalid();
        var eligible = ready ? workState.ReadyQuantity - delivery!.DeliveredQuantity : workState.InPreparationQuantity;
        if (quantity > eligible) return PreparationProgressResult.AvailableQuantityInsufficient();

        quantities!.Cancel(quantity, content.Quantity, eligible);
        if (ready) workState.InterveneReady(quantity, delivery!.DeliveredQuantity);
        else workState.InterveneInPreparation(quantity);

        var now = timeProvider.GetUtcNow();
        var occurredAt = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        var historyId = Guid.CreateVersion7(occurredAt);
        var result = new PreparationCommandResult(workId, historyId, occurredAt, workState.TotalQuantity,
            workState.PendingQuantity, workState.InPreparationQuantity, workState.ReadyQuantity);
        // Work has a restrictive FK to its exact Content. Kind + quantity + resulting P/I/Y/T
        // preserve the real source stage and the reduction of F without duplicating Content.
        dbContext.PreparationHistory.Add(ready
            ? PreparationHistory.ReadyIntervened(historyId, workId, quantity, session.IdentityId, occurredAt, result)
            : PreparationHistory.InPreparationIntervened(historyId, workId, quantity, session.IdentityId, occurredAt, result));
        dbContext.PreparationCommands.Add(ready
            ? PreparationCommand.InterveneReady(key, session.IdentityId, workId, quantity, result)
            : PreparationCommand.InterveneInPreparation(key, session.IdentityId, workId, quantity, result));
        await dbContext.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        orderInvalidations.PublishChanged(target.OrderId);
        invalidations.Publish([workState.PreparationResponsibilityId]);
        return PreparationProgressResult.Succeeded(result);
    }
}
