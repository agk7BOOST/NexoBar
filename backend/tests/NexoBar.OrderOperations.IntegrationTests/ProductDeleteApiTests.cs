using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ProductDeleteApiTests(OrderOperationsApiFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Never_confirmed_active_or_retired_Product_is_deleted_and_name_reusable(bool retire)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Reusable", "5", token);
        if (retire)
        {
            using var retired = await SendAsync(HttpMethod.Post, $"/api/catalog/products/{product.Id}/retire", Guid.NewGuid(), token);
            retired.EnsureSuccessStatusCode();
        }

        var key = Guid.NewGuid();
        using var deleted = await SendAsync(HttpMethod.Delete, $"/api/catalog/products/{product.Id}", key, token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(product.Id, (await deleted.Content.ReadFromJsonAsync<ProductDeleteResponse>(token))?.ProductId);
        using var replay = await SendAsync(HttpMethod.Delete, $"/api/catalog/products/{product.Id}", key, token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(product.Id, (await replay.Content.ReadFromJsonAsync<ProductDeleteResponse>(token))?.ProductId);
        using var changed = await SendAsync(HttpMethod.Delete, $"/api/catalog/products/{Guid.NewGuid()}", key, token);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Contains("idempotency_key_conflict", await changed.Content.ReadAsStringAsync(token));

        var replacement = await fixture.CreateProductAsync("Reusable", "7", token);
        Assert.NotEqual(product.Id, replacement.Id);
        using var missing = await SendAsync(HttpMethod.Delete, $"/api/catalog/products/{Guid.NewGuid()}", Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Confirmed_Product_remains_even_after_rejected_Delete()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Confirmed", "5", token);
        using var confirmation = await ConfirmAsync(product.Id, token);
        Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
        using var deleted = await SendAsync(HttpMethod.Delete, $"/api/catalog/products/{product.Id}", Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Contains("catalog.product.delete.confirmed_participation", await deleted.Content.ReadAsStringAsync(token));
        Assert.Equal(product.Id, (await fixture.ReadProductAsync(product.Id, token)).Id);
    }

    [Theory]
    [InlineData("content_cancelled")]
    [InlineData("completely_cancelled")]
    [InlineData("closed")]
    public async Task Later_Order_history_never_erases_confirmed_participation(string laterState)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        Guid productId;
        if (laterState == "closed")
        {
            var order = await ClosureTestSupport.CreateOrderAsync(fixture, token);
            productId = Assert.Single(await fixture.ReadConfirmedContentsAsync(token)).ProductId;
            using var closed = await ClosureTestSupport.PostAsync(
                fixture.OrderOperationsClient, order.OperationalReference, Guid.NewGuid(), token);
            closed.EnsureSuccessStatusCode();
        }
        else
        {
            var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 1, token);
            productId = Assert.Single(await fixture.ReadConfirmedContentsAsync(token)).ProductId;
            if (laterState == "content_cancelled")
            {
                using var cancelled = await ContentCancellationTestSupport.PostAsync(
                    fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
                cancelled.EnsureSuccessStatusCode();
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/orders/{target.OperationalReference}/complete-cancellation");
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
                using var cancelled = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
                    fixture.OrderOperationsClient, request, token);
                cancelled.EnsureSuccessStatusCode();
            }
        }

        using var deleted = await SendAsync(HttpMethod.Delete,
            $"/api/catalog/products/{productId}", Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Contains("confirmed_participation", await deleted.Content.ReadAsStringAsync(token));
    }

    [Fact]
    public async Task Delete_requires_CatalogConfiguration_and_active_Identity()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Authority", "5", token);
        var actor = await fixture.CreateConfirmationActorAsync(true, false, token);
        using var orderOnly = await fixture.LoginAsync(actor, token);
        using var denied = await SendAsync(orderOnly, HttpMethod.Delete, $"/api/catalog/products/{product.Id}", Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var generalActor = await fixture.CreateDeliveryActorAsync(false, false, null, token);
        await using (var generalScope = fixture.Services.CreateAsyncScope())
        {
            var db = generalScope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
            db.ResponsibilityAssignments.Add(new ResponsibilityAssignment(
                generalActor.IdentityId, FunctionalResponsibility.GeneralConfiguration));
            await db.SaveChangesAsync(token);
        }
        using var generalOnly = await fixture.LoginAsync(generalActor, token);
        using var generalDenied = await SendAsync(generalOnly, HttpMethod.Delete,
            $"/api/catalog/products/{product.Id}", Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Forbidden, generalDenied.StatusCode);

        await using var scope = fixture.Services.CreateAsyncScope();
        var identities = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        var administrator = await identities.Identities.SingleAsync(x => x.Id == fixture.DefaultOrderOperationsActor.IdentityId, token);
        administrator.Deactivate();
        await identities.SaveChangesAsync(token);
        using var inactive = await SendAsync(HttpMethod.Delete, $"/api/catalog/products/{product.Id}", Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Unauthorized, inactive.StatusCode);
    }

    [Fact]
    public async Task Confirmation_after_Delete_rejects_missing_Product()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Gone", "5", token);
        using var deleted = await SendAsync(HttpMethod.Delete, $"/api/catalog/products/{product.Id}", Guid.NewGuid(), token);
        deleted.EnsureSuccessStatusCode();
        using var confirmation = await ConfirmAsync(product.Id, token);
        Assert.Equal(HttpStatusCode.Conflict, confirmation.StatusCode);
        Assert.Contains("product_not_current", await confirmation.Content.ReadAsStringAsync(token));
    }

    [Fact]
    public async Task Unconfirmed_PendingComposition_does_not_reserve_Product_for_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var existing = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 1, token);
        await fixture.StartPendingCompositionAsync(existing.OperationalReference, token);
        var candidate = await fixture.CreateProductAsync("Pending only", "5", token);

        using var deleted = await SendAsync(HttpMethod.Delete,
            $"/api/catalog/products/{candidate.Id}", Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Single(await fixture.ReadConfirmedContentsAsync(token));
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid key, CancellationToken token) =>
        SendAsync(fixture.OrderOperationsClient, method, path, key, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, Guid key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, token);
    }

    private async Task<HttpResponseMessage> ConfirmAsync(Guid productId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
        { Content = JsonContent.Create(new FirstConfirmationRequest("Delete eligibility", [new FirstConfirmationItemRequest(productId, 1)])) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, token);
    }
}
