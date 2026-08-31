using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class PreparationWorkQueryService(
    OrderOperationsDbContext dbContext,
    IPreparationAuthorization preparationAuthorization,
    IProductOperationalReferenceLookup productReferences)
{
    internal async Task<PreparationWorkQueryResult> ListAsync(
        Guid preparationResponsibilityId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var dbTransaction = transaction.GetDbTransaction();
        var authorization = await preparationAuthorization.AuthorizeAsync(
            preparationResponsibilityId,
            dbTransaction,
            cancellationToken);
        if (authorization == PreparationAuthorizationOutcome.Unauthenticated)
        {
            return PreparationWorkQueryResult.Unauthenticated();
        }

        if (authorization == PreparationAuthorizationOutcome.Forbidden)
        {
            return PreparationWorkQueryResult.Forbidden();
        }

        var persisted = await (
            from work in dbContext.PreparationWork.AsNoTracking()
            join content in dbContext.IncorporationContents.AsNoTracking()
                on new { work.IncorporationId, work.ContentOrdinal }
                equals new { content.IncorporationId, content.ContentOrdinal }
            join incorporation in dbContext.Incorporations.AsNoTracking()
                on work.IncorporationId equals incorporation.Id
            join order in dbContext.Orders.AsNoTracking()
                on incorporation.OrderId equals order.Id
            join history in dbContext.ConfirmationHistory.AsNoTracking()
                on incorporation.Id equals history.IncorporationId
            where work.PreparationResponsibilityId == preparationResponsibilityId
            orderby history.OccurredAt,
                work.IncorporationId,
                work.ContentOrdinal,
                work.Id
            select new
            {
                WorkId = work.Id,
                work.PreparationResponsibilityId,
                OrderId = order.Id,
                order.Context,
                IncorporationId = incorporation.Id,
                IncorporationOrdinal = incorporation.Ordinal,
                content.ProductId,
                content.Instruction,
                work.TotalQuantity,
                work.PendingQuantity,
                work.InPreparationQuantity,
                work.ReadyQuantity,
                ConfirmedAt = history.OccurredAt
            }).ToArrayAsync(cancellationToken);

        var productIds = persisted.Select(work => work.ProductId).Distinct().ToArray();
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
            return PreparationWorkQueryResult.ProductReferenceInconsistent();
        }

        var response = persisted.Select(work => new PreparationWorkResponse(
            work.WorkId,
            work.PreparationResponsibilityId,
            work.OrderId.ToString("D"),
            work.Context,
            work.IncorporationId,
            work.IncorporationOrdinal,
            work.ProductId,
            namesById[work.ProductId].OperationalName,
            work.Instruction,
            work.TotalQuantity,
            work.PendingQuantity,
            work.InPreparationQuantity,
            work.ReadyQuantity,
            work.ConfirmedAt)).ToArray();
        await transaction.CommitAsync(cancellationToken);
        return PreparationWorkQueryResult.Succeeded(response);
    }
}
