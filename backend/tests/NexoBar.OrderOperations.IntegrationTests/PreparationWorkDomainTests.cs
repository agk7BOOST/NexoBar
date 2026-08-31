using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed class PreparationWorkDomainTests
{
    [Fact]
    public void Start_moves_only_the_requested_pending_quantity()
    {
        var work = CreateWork(5);

        var outcome = work.Start(2);

        Assert.Equal(PreparationStartTransition.Started, outcome);
        Assert.Equal(5, work.TotalQuantity);
        Assert.Equal(3, work.PendingQuantity);
        Assert.Equal(2, work.InPreparationQuantity);
        Assert.Equal(0, work.ReadyQuantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Start_rejects_non_positive_quantity_without_mutation(int quantity)
    {
        var work = CreateWork(5);

        var outcome = work.Start(quantity);

        Assert.Equal(PreparationStartTransition.QuantityInvalid, outcome);
        Assert.Equal(5, work.PendingQuantity);
        Assert.Equal(0, work.InPreparationQuantity);
    }

    [Fact]
    public void Start_does_not_clip_quantity_to_pending()
    {
        var work = CreateWork(3);

        var outcome = work.Start(4);

        Assert.Equal(PreparationStartTransition.PendingQuantityInsufficient, outcome);
        Assert.Equal(3, work.PendingQuantity);
        Assert.Equal(0, work.InPreparationQuantity);
    }

    private static PreparationWork CreateWork(int quantity) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        1,
        Guid.CreateVersion7(),
        quantity);
}
