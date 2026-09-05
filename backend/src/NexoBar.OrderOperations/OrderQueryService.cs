using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace NexoBar.OrderOperations;

internal sealed class OrderQueryService(
    OrderOperationsDbContext dbContext,
    OrderEconomicStateReader economicStateReader,
    ClosureStateReader closureStateReader)
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
                    content.AppliedPrice.ToString(CultureInfo.InvariantCulture),
                    content.Instruction))
                .ToArray());

        var incorporations = incorporationHeaders
            .Select(incorporation => new OrderIncorporationResponse(
                incorporation.Id,
                incorporation.Ordinal,
                incorporation.ConfirmedAt,
                contentsByIncorporation.GetValueOrDefault(incorporation.Id, [])))
            .ToArray();

        var economicState = await economicStateReader.ReadAsync(orderId, cancellationToken);
        var closureState = await closureStateReader.ReadAsync(orderId, cancellationToken);
        var liquidation = closureState.Liquidation;
        var hasPendingComposition = closureState.HasPendingComposition;
        var blockers = new List<string>();
        if (liquidation is not null)
        {
            blockers.Add(LiquidationEligibilityBlockers.AlreadyLiquidated);
        }
        if (hasPendingComposition)
        {
            blockers.Add(LiquidationEligibilityBlockers.PendingComposition);
        }
        if (economicState.IsInconsistent)
        {
            blockers.Add(LiquidationEligibilityBlockers.StateInconsistent);
        }
        else if (economicState.HasUnresolvedFulfillment)
        {
            blockers.Add(LiquidationEligibilityBlockers.UnresolvedFulfillment);
        }

        return new OrderQueryResponse(
            orderId.ToString("D"),
            context,
            incorporations,
            economicState.FunctionalAmount.ToString(CultureInfo.InvariantCulture),
            blockers.Count == 0,
            blockers,
            liquidation is not null,
            liquidation is not null,
            liquidation?.FunctionalAmount.ToString(CultureInfo.InvariantCulture),
            liquidation?.Mode,
            liquidation?.DeclaredPaymentMedium,
            closureState.Closure is not null,
            closureState.Closure?.ClosedAt,
            closureState.IsEligible);
    }
}
