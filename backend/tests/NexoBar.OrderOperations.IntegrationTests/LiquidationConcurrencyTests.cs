using System.Net;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class LiquidationConcurrencyTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Delivery_first_is_included_by_waiting_liquidation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await LiquidationTestSupport.CreateDirectOrderAsync(
            fixture,
            [("5", 2)],
            token);
        var content = Assert.Single(await fixture.ReadConfirmedContentsAsync(token));
        await fixture.SetDeliveredQuantityAsync(
            content.IncorporationId,
            content.ContentOrdinal,
            1,
            token);

        await using var blocker = await LockOrderAsync(content.OrderId, token);
        var deliveryTask = DeliveryQuantityTestSupport.PostAsync(
            fixture.OrderOperationsClient,
            content.IncorporationId,
            content.ContentOrdinal,
            Guid.NewGuid(),
            1,
            token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(
            1, TimeSpan.FromSeconds(10), token));
        var liquidationTask = LiquidationTestSupport.PostExternalAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(
            2, TimeSpan.FromSeconds(10), token));
        await blocker.CommitAsync(token);

        using var delivery = await deliveryTask;
        await DeliveryQuantityTestSupport.ReadSuccessAsync(delivery, token);
        using var liquidation = await liquidationTask;
        var result = await LiquidationTestSupport.ReadSuccessAsync(liquidation, token);
        Assert.Equal("10", result.FunctionalAmount);
    }

    [Fact]
    public async Task Liquidation_first_freezes_stale_delivery()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateFullyDeliveredOrderAsync(token);
        var content = Assert.Single(await fixture.ReadConfirmedContentsAsync(token));

        await using var blocker = await LockOrderAsync(content.OrderId, token);
        var liquidationTask = LiquidationTestSupport.PostExternalAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(
            1, TimeSpan.FromSeconds(10), token));
        var deliveryTask = DeliveryQuantityTestSupport.PostAsync(
            fixture.OrderOperationsClient,
            content.IncorporationId,
            content.ContentOrdinal,
            Guid.NewGuid(),
            1,
            token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(
            2, TimeSpan.FromSeconds(10), token));
        await blocker.CommitAsync(token);

        using var liquidation = await liquidationTask;
        await LiquidationTestSupport.ReadSuccessAsync(liquidation, token);
        using var delivery = await deliveryTask;
        await LiquidationTestSupport.AssertProblemAsync(
            delivery,
            HttpStatusCode.Conflict,
            "order_operations.order.frozen",
            token);
    }

    [Fact]
    public async Task Concurrent_liquidations_have_exactly_one_effect()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateFullyDeliveredOrderAsync(token);
        var responses = await Task.WhenAll(
            LiquidationTestSupport.PostSimpleAsync(
                fixture.OrderOperationsClient,
                order.OperationalReference,
                Guid.NewGuid(),
                "Cash",
                token),
            LiquidationTestSupport.PostExternalAsync(
                fixture.OrderOperationsClient,
                order.OperationalReference,
                Guid.NewGuid(),
                token));
        using var first = responses[0];
        using var second = responses[1];

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        var conflict = Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.Conflict);
        await LiquidationTestSupport.AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            "order_operations.order.frozen",
            token);
        Assert.Single(await fixture.ReadLiquidationsAsync(token));
        Assert.Single(await fixture.ReadLiquidationHistoryAsync(token));
        Assert.Single(await fixture.ReadLiquidationCommandsAsync(token));
    }

    [Fact]
    public async Task Different_orders_remain_concurrent()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var firstOrder = await CreateFullyDeliveredOrderAsync(token);
        var firstOrderId = Guid.Parse(firstOrder.OperationalReference);
        var secondOrder = await CreateFullyDeliveredOrderAsync(token);

        await using var blocker = await LockOrderAsync(firstOrderId, token);
        var blockedTask = LiquidationTestSupport.PostExternalAsync(
            fixture.OrderOperationsClient,
            firstOrder.OperationalReference,
            Guid.NewGuid(),
            token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(
            1, TimeSpan.FromSeconds(10), token));

        using var independent = await LiquidationTestSupport.PostExternalAsync(
            fixture.OrderOperationsClient,
            secondOrder.OperationalReference,
            Guid.NewGuid(),
            token).WaitAsync(TimeSpan.FromSeconds(10), token);
        await LiquidationTestSupport.ReadSuccessAsync(independent, token);
        Assert.False(blockedTask.IsCompleted);

        await blocker.CommitAsync(token);
        using var blocked = await blockedTask;
        await LiquidationTestSupport.ReadSuccessAsync(blocked, token);
        Assert.Equal(2, (await fixture.ReadLiquidationsAsync(token)).Count);
    }

    private async Task<FirstConfirmationResponse> CreateFullyDeliveredOrderAsync(
        CancellationToken cancellationToken)
    {
        var order = await LiquidationTestSupport.CreateDirectOrderAsync(
            fixture,
            [("5", 2)],
            cancellationToken);
        await fixture.SetAllDeliveredQuantitiesAsync(
            Guid.Parse(order.OperationalReference),
            cancellationToken);
        return order;
    }

    private async Task<NpgsqlTransaction> LockOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id FROM order_operations.orders WHERE id = @order_id FOR UPDATE";
        command.Parameters.AddWithValue("order_id", orderId);
        await command.ExecuteScalarAsync(cancellationToken);
        return transaction;
    }
}
