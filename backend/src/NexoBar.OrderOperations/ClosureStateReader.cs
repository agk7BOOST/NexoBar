using Microsoft.EntityFrameworkCore;

namespace NexoBar.OrderOperations;

internal sealed class ClosureStateReader(OrderOperationsDbContext dbContext)
{
    // One statement observes Liquidation and Closure coherently even during a concurrent Close.
    internal Task<OrderClosureState> ReadAsync(Guid orderId, CancellationToken cancellationToken) =>
        (from order in dbContext.Orders.AsNoTracking()
         where order.Id == orderId
         join liquidationValue in dbContext.Liquidations.AsNoTracking()
             on order.Id equals liquidationValue.OrderId into liquidations
         from liquidation in liquidations.DefaultIfEmpty()
         join closureValue in dbContext.Closures.AsNoTracking()
             on order.Id equals closureValue.OrderId into closures
         from closure in closures.DefaultIfEmpty()
         select new OrderClosureState(liquidation, closure,
             dbContext.PendingCompositions.Any(pending => pending.OrderId == orderId)))
        .SingleAsync(cancellationToken);
}

internal sealed record OrderClosureState(Liquidation? Liquidation, Closure? Closure, bool HasPendingComposition)
{
    internal bool IsInconsistent =>
        (Closure is not null && (Liquidation is null || Closure.ClosedAt < Liquidation.OccurredAt)) ||
        (Liquidation is not null && HasPendingComposition);

    internal bool IsEligible => Liquidation is not null && Closure is null && !IsInconsistent;
}
