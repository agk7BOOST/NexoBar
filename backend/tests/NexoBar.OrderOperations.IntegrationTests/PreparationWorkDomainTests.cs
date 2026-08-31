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

    [Fact]
    public void MarkReady_moves_only_the_requested_in_preparation_quantity()
    {
        var work = CreateWork(5);
        Assert.Equal(PreparationStartTransition.Started, work.Start(3));

        var outcome = work.MarkReady(2);

        Assert.Equal(PreparationReadyTransition.MarkedReady, outcome);
        Assert.Equal(5, work.TotalQuantity);
        Assert.Equal(2, work.PendingQuantity);
        Assert.Equal(1, work.InPreparationQuantity);
        Assert.Equal(2, work.ReadyQuantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MarkReady_rejects_non_positive_quantity_without_mutation(int quantity)
    {
        var work = CreateWork(5);
        work.Start(3);

        var outcome = work.MarkReady(quantity);

        Assert.Equal(PreparationReadyTransition.QuantityInvalid, outcome);
        Assert.Equal(2, work.PendingQuantity);
        Assert.Equal(3, work.InPreparationQuantity);
        Assert.Equal(0, work.ReadyQuantity);
    }

    [Fact]
    public void MarkReady_never_consumes_pending_or_clips_to_available_quantity()
    {
        var work = CreateWork(5);
        work.Start(2);

        var outcome = work.MarkReady(3);

        Assert.Equal(
            PreparationReadyTransition.InPreparationQuantityInsufficient,
            outcome);
        Assert.Equal(3, work.PendingQuantity);
        Assert.Equal(2, work.InPreparationQuantity);
        Assert.Equal(0, work.ReadyQuantity);
    }

    [Fact]
    public void MarkReady_can_derive_a_fully_ready_work_without_extra_state()
    {
        var work = CreateWork(5);
        work.Start(5);

        var outcome = work.MarkReady(5);

        Assert.Equal(PreparationReadyTransition.MarkedReady, outcome);
        Assert.Equal(work.TotalQuantity, work.ReadyQuantity);
        Assert.Equal(0, work.PendingQuantity);
        Assert.Equal(0, work.InPreparationQuantity);
    }

    private static PreparationWork CreateWork(int quantity) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        1,
        Guid.CreateVersion7(),
        quantity);
}
