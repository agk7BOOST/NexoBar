using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class OrderQueryService(
    OrderOperationsDbContext dbContext,
    IOrderOperationsAuthorization authorization,
    ActiveOrderReadState activeOrder,
    OrderEconomicStateReader economicStateReader,
    ClosureStateReader closureStateReader)
{
    internal async Task<OrderQueryResult> FindAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await authorization.AuthorizeAsync(transaction.GetDbTransaction(), cancellationToken);
        if (authority == OrderOperationsAuthorizationOutcome.Unauthenticated) return new(OrderQueryOutcome.Unauthenticated);
        if (authority == OrderOperationsAuthorizationOutcome.Forbidden) return new(OrderQueryOutcome.Forbidden);
        if (!await activeOrder.IsReadableAsync(orderId, cancellationToken)) return new(OrderQueryOutcome.OrderNotFound);

        var context = await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.Id == orderId)
            .Select(order => order.Context)
            .SingleOrDefaultAsync(cancellationToken);

        if (context is null)
        {
            return new(OrderQueryOutcome.OrderNotFound);
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
        if (await dbContext.OrderCancellationStates.AsNoTracking().AnyAsync(x => x.OrderId == orderId, cancellationToken))
            blockers.Add("order_completely_cancelled");
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

        var response = new OrderQueryResponse(
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
        await transaction.CommitAsync(cancellationToken);
        return new(OrderQueryOutcome.Succeeded, response);
    }
}

internal enum OrderQueryOutcome { Succeeded, Unauthenticated, Forbidden, OrderNotFound }
internal sealed record OrderQueryResult(OrderQueryOutcome Outcome, OrderQueryResponse? Response = null);
