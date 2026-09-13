using Microsoft.EntityFrameworkCore;

namespace NexoBar.OrderOperations;

// Caller first establishes current actor authority and owns the transaction.
internal sealed class ActiveOrderReadState(OrderOperationsDbContext dbContext)
{
    internal async Task<bool> IsReadableAsync(Guid orderId, CancellationToken token)
    {
        // All terminal writers lock Order first. Hold this lock through the read,
        // so Closure or Complete Cancellation cannot commit between check and State.
        if (!await dbContext.Orders.FromSqlInterpolated(
                $"SELECT id, context FROM order_operations.orders WHERE id = {orderId} FOR SHARE")
            .AsNoTracking().AnyAsync(token)) return false;

        return await dbContext.Incorporations.AsNoTracking().AnyAsync(x => x.OrderId == orderId, token) &&
            !await dbContext.Closures.AsNoTracking().AnyAsync(x => x.OrderId == orderId, token) &&
            !await dbContext.OrderCancellationStates.AsNoTracking().AnyAsync(x => x.OrderId == orderId, token);
    }
}
