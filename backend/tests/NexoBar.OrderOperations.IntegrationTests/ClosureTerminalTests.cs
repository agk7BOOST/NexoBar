using System.Net;
using System.Net.Http.Json;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ClosureTerminalTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Every_current_ordinary_mutation_stays_frozen_after_close()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibilityId = Guid.CreateVersion7();
        var created = await fixture.CreatePreparedWorkAsync(responsibilityId, token, quantity: 1);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        await fixture.SetPreparationQuantitiesAsync(work.Id, 0, 0, 1, token);
        var orderId = created.Confirmation.OperationalReference;
        await fixture.SetAllDeliveredQuantitiesAsync(Guid.Parse(orderId), token);
        var actor = await fixture.CreatePreparationActorAsync(true, responsibilityId, token);
        using var preparer = await fixture.LoginAsync(actor, token);
        var liquidationKey = Guid.NewGuid();
        using var liquidated = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, orderId, liquidationKey, token);
        var originalLiquidation = await LiquidationTestSupport.ReadSuccessAsync(liquidated, token);
        using var close = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, orderId, Guid.NewGuid(), token);
        await ClosureTestSupport.ReadSuccessAsync(close, token);

        using var start = await PreparationStartTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), 1, token);
        using var ready = await PreparationReadyTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), 1, token);
        var content = Assert.Single(await fixture.ReadConfirmedContentsAsync(token));
        using var delivery = await DeliveryQuantityTestSupport.PostAsync(fixture.OrderOperationsClient, content.IncorporationId, content.ContentOrdinal, Guid.NewGuid(), 1, token);
        using var simple = await LiquidationTestSupport.PostSimpleAsync(fixture.OrderOperationsClient, orderId, Guid.NewGuid(), "Cash", token);
        using var external = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, orderId, Guid.NewGuid(), token);
        foreach (var response in new[] { start, ready, delivery, simple, external })
            await LiquidationTestSupport.AssertProblemAsync(response, HttpStatusCode.Conflict, "order_operations.order.frozen", token);

        var requests = new[]
        {
            new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{orderId}/pending-composition"),
            new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{orderId}/pending-composition/{Guid.CreateVersion7()}/discard"),
            new HttpRequestMessage(HttpMethod.Post, $"/api/order-operations/orders/{orderId}/confirmations")
            {
                Content = JsonContent.Create(new SubsequentConfirmationRequest(Guid.CreateVersion7(),
                    [new SubsequentConfirmationItemRequest(content.ProductId, 1)]))
            }
        };
        foreach (var request in requests)
        {
            using (request)
            {
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
                using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, token);
                await LiquidationTestSupport.AssertProblemAsync(response, HttpStatusCode.Conflict, "order_operations.order.frozen", token);
            }
        }
        // Replaying an earlier economic result remains a replay, not a new effect.
        using var replay = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, orderId, liquidationKey, token);
        Assert.Equal(originalLiquidation, await LiquidationTestSupport.ReadSuccessAsync(replay, token));
        await ClosureTestSupport.AssertCountsAsync(fixture, 1, token);
        Assert.Single(await fixture.ReadLiquidationsAsync(token));
        Assert.Single(await fixture.ReadLiquidationHistoryAsync(token));
        Assert.Single(await fixture.ReadConfirmedContentsAsync(token));
        Assert.Empty(await fixture.ReadPreparationHistoryAsync(token));
    }
}
