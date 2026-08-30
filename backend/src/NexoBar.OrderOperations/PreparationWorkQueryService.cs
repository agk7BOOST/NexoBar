using Microsoft.EntityFrameworkCore;

namespace NexoBar.OrderOperations;

internal sealed class PreparationWorkQueryService(OrderOperationsDbContext dbContext)
{
    internal async Task<IReadOnlyList<PreparationWorkResponse>> ListAsync(
        Guid preparationResponsibilityId,
        CancellationToken cancellationToken)
    {
        var persisted = await (
            from work in dbContext.PreparationWork.AsNoTracking()
            join incorporation in dbContext.Incorporations.AsNoTracking()
                on work.IncorporationId equals incorporation.Id
            join order in dbContext.Orders.AsNoTracking()
                on incorporation.OrderId equals order.Id
            join history in dbContext.ConfirmationHistory.AsNoTracking()
                on incorporation.Id equals history.IncorporationId
            where work.PreparationResponsibilityId == preparationResponsibilityId
            orderby history.OccurredAt,
                work.IncorporationId,
                work.ProductId,
                work.Id
            select new
            {
                WorkId = work.Id,
                work.PreparationResponsibilityId,
                OrderId = order.Id,
                order.Context,
                IncorporationId = incorporation.Id,
                IncorporationOrdinal = incorporation.Ordinal,
                work.ProductId,
                work.TotalQuantity,
                work.PendingQuantity,
                work.InPreparationQuantity,
                work.ReadyQuantity,
                ConfirmedAt = history.OccurredAt
            }).ToArrayAsync(cancellationToken);

        return persisted.Select(work => new PreparationWorkResponse(
            work.WorkId,
            work.PreparationResponsibilityId,
            work.OrderId.ToString("D"),
            work.Context,
            work.IncorporationId,
            work.IncorporationOrdinal,
            work.ProductId,
            work.TotalQuantity,
            work.PendingQuantity,
            work.InPreparationQuantity,
            work.ReadyQuantity,
            work.ConfirmedAt)).ToArray();
    }
}
