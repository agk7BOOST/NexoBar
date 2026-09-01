using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed class DeliveryStateDomainTests
{
    [Fact]
    public void Deliver_increments_exact_quantity_within_authoritative_limit()
    {
        var state = new DeliveryState(Guid.CreateVersion7(), 1);

        var transition = state.Deliver(2, 3);

        Assert.Equal(DeliveryTransition.Delivered, transition);
        Assert.Equal(2, state.DeliveredQuantity);
    }

    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(-1, 3, 1)]
    [InlineData(4, 3, 2)]
    public void Rejected_delivery_does_not_mutate(
        int quantity,
        int deliverable,
        int expected)
    {
        var state = new DeliveryState(Guid.CreateVersion7(), 1);

        var transition = state.Deliver(quantity, deliverable);

        Assert.Equal((DeliveryTransition)expected, transition);
        Assert.Equal(0, state.DeliveredQuantity);
    }
}
