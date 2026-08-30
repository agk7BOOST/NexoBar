using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace NexoBar.OrderOperations;

internal sealed class OrderQueryService(OrderOperationsDbContext dbContext)
{
    internal async Task<OrderQueryResponse?> FindAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var context = await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.Id == orderId)
            .Select(order => order.Context)
            .SingleOrDefaultAsync(cancellationToken);

        if (context is null)
        {
            return null;
        }

        var incorporationHeaders = await (
            from incorporation in dbContext.Incorporations.AsNoTracking()
            join history in dbContext.ConfirmationHistory.AsNoTracking()
                on incorporation.Id equals history.IncorporationId
            where incorporation.OrderId == orderId
            orderby incorporation.Ordinal
            select new
            {
                incorporation.Id,
                incorporation.Ordinal,
                ConfirmedAt = history.OccurredAt
            }).ToArrayAsync(cancellationToken);

        var incorporationIds = incorporationHeaders
            .Select(incorporation => incorporation.Id)
            .ToArray();
        var persistedContents = incorporationIds.Length == 0
            ? []
            : await dbContext.IncorporationContents
                .AsNoTracking()
                .Where(content => incorporationIds.Contains(content.IncorporationId))
                .OrderBy(content => content.IncorporationId)
                .ThenBy(content => content.ContentOrdinal)
                .ToArrayAsync(cancellationToken);
        var contentsByIncorporation = persistedContents
            .GroupBy(content => content.IncorporationId)
            .ToDictionary(group => group.Key, group => group
                .Select(content => new ConfirmedItemResponse(
                    content.ProductId,
                    content.Quantity,
                    content.AppliedPrice.ToString(CultureInfo.InvariantCulture)))
                .ToArray());

        var incorporations = incorporationHeaders
            .Select(incorporation => new OrderIncorporationResponse(
                incorporation.Id,
                incorporation.Ordinal,
                incorporation.ConfirmedAt,
                contentsByIncorporation.GetValueOrDefault(incorporation.Id, [])))
            .ToArray();

        return new OrderQueryResponse(orderId.ToString("D"), context, incorporations);
    }
}
