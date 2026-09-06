using System.Net;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class DeliveryCorrectionConcurrencyTests(OrderOperationsApiFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Correction_and_delivery_observe_order_coordination(bool correctionFirst)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 2, token);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, token);
        Task<HttpResponseMessage> Correct() => DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 3, token);
        Task<HttpResponseMessage> Deliver() => DeliveryQuantityTestSupport.PostAsync(fixture.OrderOperationsClient, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 1, token);
        var first = correctionFirst ? Correct() : Deliver();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
        var second = correctionFirst ? Deliver() : Correct();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), token));
        await blocker.CommitAsync(token);
        using var firstResponse = await first;
        using var secondResponse = await second;
        var correction = correctionFirst ? firstResponse : secondResponse;
        if (correctionFirst) await DeliveryCorrectionTestSupport.ProblemAsync(correction, HttpStatusCode.Conflict, "quantity_exceeds_delivered", token);
        else Assert.Equal(3, (await DeliveryCorrectionTestSupport.SuccessAsync(correction, token)).PreviousDeliveredQuantity);
        Assert.Equal(HttpStatusCode.OK, (correctionFirst ? secondResponse : firstResponse).StatusCode);
        Assert.Equal(correctionFirst ? 3 : 0, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Correction_and_liquidation_observe_frozen_frontier(bool correctionFirst)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 3, token);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, token);
        Task<HttpResponseMessage> Correct() => DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        Task<HttpResponseMessage> Liquidate() => LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), token);
        var first = correctionFirst ? Correct() : Liquidate();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
        var second = correctionFirst ? Liquidate() : Correct();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), token));
        await blocker.CommitAsync(token);
        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        await DeliveryQuantityTestSupport.AssertProblemAsync(secondResponse, HttpStatusCode.Conflict,
            correctionFirst ? "order_operations.liquidation.unresolved_fulfillment" : "order_operations.order.frozen", token);
        var read = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, token);
        Assert.Equal(correctionFirst ? "10" : "15", read.FunctionalAmount);
        Assert.Equal(!correctionFirst, read.IsFrozen);
        await DeliveryCorrectionTestSupport.CountsAsync(fixture, correctionFirst ? 1 : 0, token);
    }

    [Theory]
    [InlineData(4, false, 2, 0)]
    [InlineData(3, false, 1, 1)]
    [InlineData(3, true, 2, 1)]
    public async Task Concurrent_corrections_validate_exact_quantity_and_deduplicate(int delivered, bool sameKey, int successes, int remaining)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 4, 4, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, delivered, token);
        var key = Guid.NewGuid();
        var responses = await Task.WhenAll(
            DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 2, token),
            DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, sameKey ? key : Guid.NewGuid(), 2, token));
        using var first = responses[0];
        using var second = responses[1];
        Assert.Equal(successes, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        if (successes == 1) await DeliveryCorrectionTestSupport.ProblemAsync(responses.Single(x => x.StatusCode != HttpStatusCode.OK), HttpStatusCode.Conflict, "quantity_exceeds_delivered", token);
        if (sameKey) Assert.Equal(await DeliveryCorrectionTestSupport.SuccessAsync(first, token), await DeliveryCorrectionTestSupport.SuccessAsync(second, token));
        Assert.Equal(remaining, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
        await DeliveryCorrectionTestSupport.CountsAsync(fixture, sameKey ? 1 : successes, token);
    }

    [Fact]
    public async Task Different_orders_remain_independent()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var first = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, first, 3, token);
        var other = await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("5", 3)], token);
        var content = (await fixture.ReadConfirmedContentsAsync(token)).Single(x => x.OrderId == Guid.Parse(other.OperationalReference));
        var second = new DeliveryTarget(other.OperationalReference, content.IncorporationId, content.ContentOrdinal, null);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, second, 3, token);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, first.OperationalReference, token);
        var blocked = DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, first, Guid.NewGuid(), 1, token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
        using var independent = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, second, Guid.NewGuid(), 1, token).WaitAsync(TimeSpan.FromSeconds(10), token);
        await DeliveryCorrectionTestSupport.SuccessAsync(independent, token);
        Assert.False(blocked.IsCompleted);
        await blocker.CommitAsync(token);
        using var response = await blocked;
        await DeliveryCorrectionTestSupport.SuccessAsync(response, token);
    }
}
