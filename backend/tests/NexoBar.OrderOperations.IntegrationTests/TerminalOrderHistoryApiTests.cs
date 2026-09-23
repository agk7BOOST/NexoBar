using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class TerminalOrderHistoryApiTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Closed_order_has_dedicated_read_after_active_read_ends()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token);
        await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, Guid.NewGuid(), token);
        using var active = await fixture.OrderOperationsClient.GetAsync($"/api/order-operations/orders/{order.OperationalReference}", token);
        Assert.Equal(HttpStatusCode.NotFound, active.StatusCode);
        using var response = await fixture.OrderOperationsClient.GetAsync($"/api/order-operations/order-history/{order.OperationalReference}", token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TerminalOrderHistory>(token);
        Assert.NotNull(body);
        Assert.Equal(Guid.Parse(order.OperationalReference), body.OrderId);
        Assert.Equal("Closure", body.Termination.Type);
        Assert.NotNull(body.Liquidation);
        Assert.NotNull(body.Closure);
        Assert.Null(body.CompleteCancellation);
        Assert.Single(body.Incorporations);
        Assert.Equal("5", body.Incorporations[0].Contents[0].AppliedPrice);
    }

    [Fact]
    public async Task Open_or_frozen_order_is_not_available_as_terminal_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var open = await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("5", 2)], token);
        using var response = await fixture.OrderOperationsClient.GetAsync($"/api/order-operations/order-history/{open.OperationalReference}", token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var malformed = await fixture.OrderOperationsClient.GetAsync("/api/order-operations/order-history/garbage", token);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var frozen = await ClosureTestSupport.CreateOrderAsync(fixture, token, liquidate: true);
        using var frozenResponse = await fixture.OrderOperationsClient.GetAsync($"/api/order-operations/order-history/{frozen.OperationalReference}", token);
        Assert.Equal(HttpStatusCode.NotFound, frozenResponse.StatusCode);
    }

    [Theory]
    [InlineData("none", HttpStatusCode.Forbidden)]
    [InlineData("preparation", HttpStatusCode.Forbidden)]
    [InlineData("intervention", HttpStatusCode.Forbidden)]
    [InlineData("operations", HttpStatusCode.OK)]
    [InlineData("inactive", HttpStatusCode.Unauthorized)]
    public async Task History_requires_active_identity_and_OABC_before_discovery(string authority, HttpStatusCode expected)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token);
        using (var closed = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, Guid.NewGuid(), token)) closed.EnsureSuccessStatusCode();
        var actor = await fixture.CreateDeliveryActorAsync(authority is "operations" or "inactive", authority == "preparation", null, token);
        using var client = await fixture.LoginAsync(actor, token);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
            if (authority == "intervention")
            {
                db.ResponsibilityAssignments.Add(new(actor.IdentityId, FunctionalResponsibility.OperationalIntervention));
                await db.SaveChangesAsync(token);
            }
            if (authority == "inactive")
                await db.Identities.Where(x => x.Id == actor.IdentityId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), token);
        }
        using var response = await client.GetAsync($"/api/order-operations/order-history/{order.OperationalReference}", token);
        Assert.Equal(expected, response.StatusCode);
        using var unknown = await client.GetAsync($"/api/order-operations/order-history/{Guid.NewGuid():D}", token);
        Assert.Equal(expected == HttpStatusCode.Unauthorized ? expected : expected == HttpStatusCode.Forbidden ? expected : HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task History_keeps_confirmation_context_change_and_final_context_separate()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var a = await fixture.EnsureConfiguredTestContextAsync("History context A", token);
        var b = await fixture.EnsureConfiguredTestContextAsync("History context B", token);
        var product = await fixture.CreateProductAsync("History context product", "5", token);
        using var client = await fixture.LoginWithoutLegacyRequestAdapterAsync(fixture.DefaultOrderOperationsActor, token);
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
        { Content = JsonContent.Create(new FirstConfirmationRequest(a, null, [new FirstConfirmationItemRequest(product.Id, 1)])) };
        firstRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var first = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, firstRequest, token);
        first.EnsureSuccessStatusCode();
        var confirmation = Assert.IsType<FirstConfirmationResponse>(await first.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
        var orderId = Guid.Parse(confirmation.OperationalReference);
        using var changeRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/order-operations/orders/{orderId}/context-changes")
        { Content = JsonContent.Create(new OrderContextChangeRequest(a, b)) };
        changeRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var changed = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, changeRequest, token);
        changed.EnsureSuccessStatusCode();
        await fixture.SetAllDeliveredQuantitiesAsync(orderId, token);
        using (var liquidated = await LiquidationTestSupport.PostExternalAsync(client, orderId.ToString("D"), Guid.NewGuid(), token)) liquidated.EnsureSuccessStatusCode();
        using (var closed = await ClosureTestSupport.PostAsync(client, orderId.ToString("D"), Guid.NewGuid(), token)) closed.EnsureSuccessStatusCode();
        using var response = await client.GetAsync($"/api/order-operations/order-history/{orderId:D}", token);
        response.EnsureSuccessStatusCode();
        var history = Assert.IsType<TerminalOrderHistory>(await response.Content.ReadFromJsonAsync<TerminalOrderHistory>(token));
        var incorporation = Assert.Single(history.Incorporations);
        Assert.Equal(a, incorporation.ConfirmedContextId);
        Assert.Equal("History context A", incorporation.ConfirmedContext);
        Assert.Equal(b, history.FinalContextId);
        Assert.Equal("History context B", history.FinalContextOperationalName);
        Assert.Equal((a, b), (Assert.Single(history.ContextChanges).PreviousContextId, history.ContextChanges[0].NewContextId));
    }

    [Fact]
    public async Task History_uses_confirmed_product_name_and_preserves_legacy_null()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Historical A", "5", token);
        var order = await CreateOrderAsync(product.Id, token);
        await fixture.RenameProductDurablyAsync(product.Id, "Historical A", "Historical B", token);
        await EndOrderAsync(order.OperationalReference, token);
        var renamedHistory = await ReadHistoryAsync(order.OperationalReference, token);
        Assert.Equal("Historical A", Assert.Single(renamedHistory.Incorporations).Contents[0].ProductOperationalNameSnapshot);

        var legacyProduct = await fixture.CreateProductAsync("Legacy historical name", "5", token);
        var legacyOrder = await CreateOrderAsync(legacyProduct.Id, token);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            await db.IncorporationContents.Where(x => x.ProductId == legacyProduct.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ProductOperationalNameSnapshot, (string?)null), token);
        }
        await EndOrderAsync(legacyOrder.OperationalReference, token);
        var legacyHistory = await ReadHistoryAsync(legacyOrder.OperationalReference, token);
        var legacyContent = Assert.Single(legacyHistory.Incorporations).Contents[0];
        Assert.Equal(legacyProduct.Id, legacyContent.ProductId);
        Assert.Null(legacyContent.ProductOperationalNameSnapshot);
    }

    private async Task<FirstConfirmationResponse> CreateOrderAsync(Guid productId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
        { Content = JsonContent.Create(new FirstConfirmationRequest("Mesa Histórica", [new FirstConfirmationItemRequest(productId, 1)])) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, token);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<FirstConfirmationResponse>(await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
    }

    private async Task EndOrderAsync(string reference, CancellationToken token)
    {
        var orderId = Guid.Parse(reference);
        await fixture.SetAllDeliveredQuantitiesAsync(orderId, token);
        using (var liquidated = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, reference, Guid.NewGuid(), token)) liquidated.EnsureSuccessStatusCode();
        using (var closed = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, reference, Guid.NewGuid(), token)) closed.EnsureSuccessStatusCode();
    }

    private async Task<TerminalOrderHistory> ReadHistoryAsync(string reference, CancellationToken token)
    {
        using var response = await fixture.OrderOperationsClient.GetAsync($"/api/order-operations/order-history/{reference}", token);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<TerminalOrderHistory>(await response.Content.ReadFromJsonAsync<TerminalOrderHistory>(token));
    }
}
