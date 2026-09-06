using NexoBar.OrderOperations;
using Microsoft.EntityFrameworkCore;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed class ContentQuantityStateDomainTests
{
    [Fact]
    public void New_state_preserves_confirmed_quantity_as_effective_obligation()
    {
        var state = new ContentQuantityState(Guid.CreateVersion7(), 1);

        Assert.Equal(0, state.RemovedByCorrectionQuantity);
        Assert.Equal(5, 5 - state.RemovedByCorrectionQuantity);
    }

    [Fact]
    public void Order_operations_model_matches_latest_snapshot()
    {
        var options = new DbContextOptionsBuilder<OrderOperationsDbContext>()
            .UseNpgsql("Host=localhost;Database=model_check;Username=model_check;Password=model_check")
            .Options;
        using var context = new OrderOperationsDbContext(options);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
