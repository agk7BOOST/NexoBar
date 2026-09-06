using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class OrderDeliveryQueryService(
    OrderOperationsDbContext dbContext,
    IOrderOperationsAuthorization authorization,
    IProductOperationalReferenceLookup productReferences,
    ILogger<OrderDeliveryQueryService> logger)
{
    internal async Task<OrderDeliveryQueryResult> FindAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var dbTransaction = transaction.GetDbTransaction();
        var authorizationOutcome = await authorization.AuthorizeAsync(
            dbTransaction,
            cancellationToken);
        if (authorizationOutcome == OrderOperationsAuthorizationOutcome.Unauthenticated)
        {
            return OrderDeliveryQueryResult.Unauthenticated();
        }

        if (authorizationOutcome == OrderOperationsAuthorizationOutcome.Forbidden)
        {
            return OrderDeliveryQueryResult.Forbidden();
        }

        var persisted = await (
            from order in dbContext.Orders.AsNoTracking()
            where order.Id == orderId
            join incorporationValue in dbContext.Incorporations.AsNoTracking()
                on order.Id equals incorporationValue.OrderId into incorporations
            from incorporation in incorporations.DefaultIfEmpty()
            join contentValue in dbContext.IncorporationContents.AsNoTracking()
                on incorporation.Id equals contentValue.IncorporationId into contents
            from content in contents.DefaultIfEmpty()
            join stateValue in dbContext.DeliveryStates.AsNoTracking()
                on new
                {
                    IncorporationId = (Guid?)content.IncorporationId,
                    ContentOrdinal = (int?)content.ContentOrdinal
                }
                equals new
                {
                    IncorporationId = (Guid?)stateValue.IncorporationId,
                    ContentOrdinal = (int?)stateValue.ContentOrdinal
                } into states
            from state in states.DefaultIfEmpty()
            join quantityStateValue in dbContext.ContentQuantityStates.AsNoTracking()
                on new
                {
                    IncorporationId = (Guid?)content.IncorporationId,
                    ContentOrdinal = (int?)content.ContentOrdinal
                }
                equals new
                {
                    IncorporationId = (Guid?)quantityStateValue.IncorporationId,
                    ContentOrdinal = (int?)quantityStateValue.ContentOrdinal
                } into quantityStates
            from quantityState in quantityStates.DefaultIfEmpty()
            join workValue in dbContext.PreparationWork.AsNoTracking()
                on new
                {
                    IncorporationId = (Guid?)content.IncorporationId,
                    ContentOrdinal = (int?)content.ContentOrdinal
                }
                equals new
                {
                    IncorporationId = (Guid?)workValue.IncorporationId,
                    ContentOrdinal = (int?)workValue.ContentOrdinal
                } into works
            from work in works.DefaultIfEmpty()
            orderby incorporation.Ordinal, content.ContentOrdinal
            select new DeliverySnapshotRow(
                order.Id,
                order.Context,
                (Guid?)incorporation.Id,
                (int?)incorporation.Ordinal,
                (int?)content.ContentOrdinal,
                (Guid?)content.ProductId,
                (int?)content.Quantity,
                quantityState == null ? null : quantityState.RemovedByCorrectionQuantity,
                content == null
                    ? null
                    : content.RequiresPreparationAtConfirmation,
                content.Instruction,
                state == null ? null : state.DeliveredQuantity,
                work == null ? null : work.Id,
                work == null ? null : work.TotalQuantity,
                work == null ? null : work.PendingQuantity,
                work == null ? null : work.InPreparationQuantity,
                work == null ? null : work.ReadyQuantity))
            .ToArrayAsync(cancellationToken);

        if (persisted.Length == 0)
        {
            return OrderDeliveryQueryResult.OrderNotFound();
        }

        var contentRows = persisted.Where(row => row.ContentOrdinal is not null).ToArray();
        if (contentRows.Length != persisted.Length ||
            contentRows.Any(IsStructurallyOrQuantitativelyInconsistent))
        {
            logger.LogError(
                "Delivery state is inconsistent for Order {OrderId}.",
                orderId);
            return OrderDeliveryQueryResult.StateInconsistent();
        }

        var productIds = contentRows
            .Select(row => row.ProductId!.Value)
            .Distinct()
            .ToArray();
        var productNames = productIds.Length == 0
            ? []
            : await productReferences.ReadByIdsAsync(
                productIds,
                dbTransaction,
                cancellationToken);
        var namesById = productNames.ToDictionary(product => product.ProductId);
        if (namesById.Count != productIds.Length ||
            productIds.Any(productId => !namesById.ContainsKey(productId)))
        {
            logger.LogError(
                "Delivery for Order {OrderId} references Products missing from Catalog.",
                orderId);
            return OrderDeliveryQueryResult.ProductReferenceInconsistent();
        }

        var contentsResponse = contentRows.Select(row =>
        {
            var total = row.ConfirmedQuantity!.Value - row.RemovedByCorrectionQuantity!.Value;
            var delivered = row.DeliveredQuantity!.Value;
            var requiresPreparation = row.RequiresPreparationAtConfirmation!.Value;
            var ready = requiresPreparation ? row.ReadyQuantity!.Value : (int?)null;
            return new OrderDeliveryContentResponse(
                row.IncorporationId!.Value,
                row.IncorporationOrdinal!.Value,
                row.ContentOrdinal!.Value,
                row.ProductId!.Value,
                namesById[row.ProductId.Value].OperationalName,
                row.Instruction,
                total,
                requiresPreparation,
                ready,
                delivered,
                requiresPreparation ? ready!.Value - delivered : total - delivered,
                total - delivered,
                row.ConfirmedQuantity.Value,
                row.RemovedByCorrectionQuantity.Value,
                total);
        }).ToArray();

        var first = persisted[0];
        var response = new OrderDeliveryResponse(
            first.OrderId,
            first.OrderId.ToString("D"),
            first.CurrentContext,
            contentsResponse);
        await transaction.CommitAsync(cancellationToken);
        return OrderDeliveryQueryResult.Succeeded(response);
    }

    private static bool IsStructurallyOrQuantitativelyInconsistent(
        DeliverySnapshotRow row)
    {
        if (row.IncorporationId is null ||
            row.IncorporationOrdinal is null ||
            row.ProductId is null ||
            row.ConfirmedQuantity is null ||
            row.RemovedByCorrectionQuantity is null ||
            row.RequiresPreparationAtConfirmation is null ||
            row.DeliveredQuantity is null)
        {
            return true;
        }

        var requiresPreparation = row.RequiresPreparationAtConfirmation.Value;
        var hasWork = row.WorkId is not null;
        if (requiresPreparation != hasWork)
        {
            return true;
        }

        var confirmed = row.ConfirmedQuantity.Value;
        var removed = row.RemovedByCorrectionQuantity.Value;
        if (confirmed <= 0 || removed < 0 || removed > confirmed)
        {
            return true;
        }

        var effective = confirmed - removed;
        var delivered = row.DeliveredQuantity.Value;
        if (effective < 0 || delivered < 0 || delivered > effective)
        {
            return true;
        }

        return requiresPreparation &&
            (row.WorkTotalQuantity != effective ||
             row.WorkPendingQuantity < 0 ||
             row.WorkInPreparationQuantity < 0 ||
             row.ReadyQuantity < 0 ||
             (long)row.WorkPendingQuantity!.Value + row.WorkInPreparationQuantity!.Value +
                 row.ReadyQuantity!.Value != effective ||
             delivered > row.ReadyQuantity.Value);
    }

    private sealed record DeliverySnapshotRow(
        Guid OrderId,
        string CurrentContext,
        Guid? IncorporationId,
        int? IncorporationOrdinal,
        int? ContentOrdinal,
        Guid? ProductId,
        int? ConfirmedQuantity,
        int? RemovedByCorrectionQuantity,
        bool? RequiresPreparationAtConfirmation,
        string? Instruction,
        int? DeliveredQuantity,
        Guid? WorkId,
        int? WorkTotalQuantity,
        int? WorkPendingQuantity,
        int? WorkInPreparationQuantity,
        int? ReadyQuantity);
}
