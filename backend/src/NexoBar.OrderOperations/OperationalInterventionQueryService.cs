using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class OperationalInterventionQueryService(
    OrderOperationsDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOperationalInterventionCapabilityStabilizer capabilityStabilizer,
    IProductOperationalReferenceLookup productReferences)
{
    internal async Task<OperationalInterventionQueryResult> FindAsync(Guid incorporationId, int ordinal, CancellationToken token)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var session = await sessionStabilizer.StabilizeAsync(transaction.GetDbTransaction(), token);
        if (session is null) return new(PreparationProgressOutcome.Unauthenticated);
        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(session.IdentityId, transaction.GetDbTransaction(), token))
            return new(PreparationProgressOutcome.Forbidden);
        // A single statement snapshots all fulfillment quantities and Freeze for this exact Content.
        var target = await (from content in dbContext.IncorporationContents.AsNoTracking()
            join incorporation in dbContext.Incorporations.AsNoTracking() on content.IncorporationId equals incorporation.Id
            join work in dbContext.PreparationWork.AsNoTracking()
                on new { content.IncorporationId, content.ContentOrdinal } equals new { work.IncorporationId, work.ContentOrdinal } into works
            from work in works.DefaultIfEmpty()
            join delivery in dbContext.DeliveryStates.AsNoTracking()
                on new { content.IncorporationId, content.ContentOrdinal } equals new { delivery.IncorporationId, delivery.ContentOrdinal } into deliveries
            from delivery in deliveries.DefaultIfEmpty()
            join quantities in dbContext.ContentQuantityStates.AsNoTracking()
                on new { content.IncorporationId, content.ContentOrdinal } equals new { quantities.IncorporationId, quantities.ContentOrdinal } into quantityStates
            from quantities in quantityStates.DefaultIfEmpty()
            where content.IncorporationId == incorporationId && content.ContentOrdinal == ordinal
            select new { incorporation.OrderId, Content = content, Work = work, Delivery = delivery, Quantities = quantities,
                Frozen = dbContext.Liquidations.Any(x => x.OrderId == incorporation.OrderId) }).SingleOrDefaultAsync(token);
        if (target is null) return new(PreparationProgressOutcome.WorkNotFound);
        if (!OperationalInterventionState.IsCoherent(target.Content, target.Work, target.Delivery, target.Quantities))
            return new(PreparationProgressOutcome.StateInconsistent);
        if (target.Work is null) return new(PreparationProgressOutcome.WorkNotFound);
        var products = await productReferences.ReadByIdsAsync([target.Content.ProductId], transaction.GetDbTransaction(), token);
        var product = products.SingleOrDefault(x => x.ProductId == target.Content.ProductId);
        if (product is null) return new(PreparationProgressOutcome.StateInconsistent);
        var response = new OperationalInterventionTargetResponse(target.OrderId, target.Work.Id, incorporationId, ordinal,
            target.Content.ProductId, product.OperationalName, target.Content.Instruction,
            target.Content.Quantity, target.Quantities.RemovedByCorrectionQuantity, target.Quantities.CancelledQuantity, target.Work.TotalQuantity,
            target.Work.PendingQuantity, target.Work.InPreparationQuantity, target.Work.ReadyQuantity, target.Work.TotalQuantity,
            target.Delivery.DeliveredQuantity, target.Frozen,
            target.Frozen ? 0 : target.Work.InPreparationQuantity,
            target.Frozen ? 0 : target.Work.ReadyQuantity - target.Delivery.DeliveredQuantity);
        await transaction.CommitAsync(token);
        return new(PreparationProgressOutcome.Succeeded, response);
    }
}

internal sealed record OperationalInterventionQueryResult(PreparationProgressOutcome Outcome, OperationalInterventionTargetResponse? Response = null);
