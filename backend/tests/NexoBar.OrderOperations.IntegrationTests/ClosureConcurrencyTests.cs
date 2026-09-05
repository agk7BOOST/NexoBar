using System.Net;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ClosureConcurrencyTests(OrderOperationsApiFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_close_has_one_effect_and_same_key_replays(bool sameKey)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token);
        var key = Guid.NewGuid();
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, order.OperationalReference, token);
        var firstTask = ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, key, token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
        var secondTask = ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, sameKey ? key : Guid.NewGuid(), token);
        if (!sameKey)
            Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), token));
        await blocker.CommitAsync(token);
        using var first = await firstTask;
        using var second = await secondTask;
        var original = await ClosureTestSupport.ReadSuccessAsync(first, token);
        if (sameKey)
            Assert.Equal(original, await ClosureTestSupport.ReadSuccessAsync(second, token));
        else
            await ClosureTestSupport.AssertProblemAsync(second, HttpStatusCode.Conflict, "already_closed", token);
        await ClosureTestSupport.AssertCountsAsync(fixture, 1, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Liquidation_and_close_observe_order_lock_ordering(bool liquidationFirst)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token, liquidate: false);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, order.OperationalReference, token);
        Task<HttpResponseMessage> Liquidate() => LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, order.OperationalReference, Guid.NewGuid(), token);
        Task<HttpResponseMessage> Close() => ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, Guid.NewGuid(), token);
        var first = liquidationFirst ? Liquidate() : Close();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
        var second = liquidationFirst ? Close() : Liquidate();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), token));
        await blocker.CommitAsync(token);
        using var liquidation = await (liquidationFirst ? first : second);
        using var closure = await (liquidationFirst ? second : first);
        await LiquidationTestSupport.ReadSuccessAsync(liquidation, token);
        if (liquidationFirst)
            await ClosureTestSupport.ReadSuccessAsync(closure, token);
        else
            await ClosureTestSupport.AssertProblemAsync(closure, HttpStatusCode.Conflict, "not_liquidated", token);
        await ClosureTestSupport.AssertCountsAsync(fixture, liquidationFirst ? 1 : 0, token);
    }

    [Fact]
    public async Task Replay_does_not_wait_for_order_and_new_close_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token);
        var key = Guid.NewGuid();
        using var initial = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, key, token);
        var original = await ClosureTestSupport.ReadSuccessAsync(initial, token);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, order.OperationalReference, token);
        var newIntent = ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, Guid.NewGuid(), token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
        using var replay = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, key, token)
            .WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(original, await ClosureTestSupport.ReadSuccessAsync(replay, token));
        Assert.False(newIntent.IsCompleted);
        await blocker.CommitAsync(token);
        using var conflict = await newIntent;
        await ClosureTestSupport.AssertProblemAsync(conflict, HttpStatusCode.Conflict, "already_closed", token);
        await ClosureTestSupport.AssertCountsAsync(fixture, 1, token);
    }

    [Fact]
    public async Task Different_orders_remain_concurrent()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var firstOrder = await ClosureTestSupport.CreateOrderAsync(fixture, token);
        var secondOrder = await ClosureTestSupport.CreateOrderAsync(fixture, token);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, firstOrder.OperationalReference, token);
        var blocked = ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, firstOrder.OperationalReference, Guid.NewGuid(), token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
        using var independent = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, secondOrder.OperationalReference, Guid.NewGuid(), token)
            .WaitAsync(TimeSpan.FromSeconds(10), token);
        await ClosureTestSupport.ReadSuccessAsync(independent, token);
        Assert.False(blocked.IsCompleted);
        await blocker.CommitAsync(token);
        using var response = await blocked;
        await ClosureTestSupport.ReadSuccessAsync(response, token);
        await ClosureTestSupport.AssertCountsAsync(fixture, 2, token);
    }
}
