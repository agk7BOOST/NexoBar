using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ProductRetirementConcurrencyTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Confirmation_snapshot_first_blocks_retirement_until_confirmation_commits()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Concurrent", "5", token);
        var snapshotHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConfirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithCatalogDecorator(services =>
            new HoldingCatalogSnapshot(
                new OrderConfirmationCatalog(services.GetRequiredService<CatalogDbContext>()),
                snapshotHeld, releaseConfirmation));
        using var client = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, token, application);

        var confirmationTask = PostConfirmationAsync(client, product.Id, token);
        try
        {
            await snapshotHeld.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var retireTask = PostRetireAsync(client, product.Id, token);
            Assert.True(await fixture.WaitForCatalogProductUpdateLockAsync(TimeSpan.FromSeconds(10), token));

            releaseConfirmation.TrySetResult();
            using var confirmation = await confirmationTask.WaitAsync(TimeSpan.FromSeconds(10), token);
            using var retired = await retireTask.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
            Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
            Assert.False((await fixture.ReadProductAsync(product.Id, token)).IsActive);
            Assert.Single(await fixture.ReadConfirmedContentsAsync(token));
        }
        finally { releaseConfirmation.TrySetResult(); }
    }

    [Fact]
    public async Task Retirement_first_makes_later_confirmation_reject_as_product_not_current()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Retired", "5", token);
        using (var retired = await PostRetireAsync(fixture.OrderOperationsClient, product.Id, token))
        {
            retired.EnsureSuccessStatusCode();
        }
        using var confirmation = await PostConfirmationAsync(fixture.OrderOperationsClient, product.Id, token);
        await DeliveryQuantityTestSupport.AssertProblemAsync(confirmation, HttpStatusCode.Conflict,
            "order_operations.confirmation.product_not_current", token);
        Assert.Empty(await fixture.ReadConfirmedContentsAsync(token));
    }

    [Fact]
    public async Task Confirmation_snapshot_first_blocks_availability_change_until_confirmation_commits()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Available race", "5", token);
        var snapshotHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConfirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithCatalogDecorator(services =>
            new HoldingCatalogSnapshot(
                new OrderConfirmationCatalog(services.GetRequiredService<CatalogDbContext>()),
                snapshotHeld,
                releaseConfirmation));
        var actor = await fixture.CreateConfirmationActorAsync(true, true, token);
        using var client = await fixture.LoginAsync(actor, token, application);

        var confirmationTask = PostConfirmationAsync(client, product.Id, token);
        try
        {
            await snapshotHeld.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var availabilityTask = PostAvailabilityAsync(client, product.Id, true, false, token);
            Assert.True(await fixture.WaitForCatalogProductUpdateLockAsync(
                TimeSpan.FromSeconds(10), token));

            releaseConfirmation.TrySetResult();
            using var confirmation = await confirmationTask.WaitAsync(
                TimeSpan.FromSeconds(10), token);
            using var unavailable = await availabilityTask.WaitAsync(
                TimeSpan.FromSeconds(10), token);
            Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
            Assert.Equal(HttpStatusCode.OK, unavailable.StatusCode);
            Assert.False((await fixture.ReadProductAsync(product.Id, token)).IsAvailable);
            Assert.Single(await fixture.ReadConfirmedContentsAsync(token));
        }
        finally
        {
            releaseConfirmation.TrySetResult();
        }
    }

    [Fact]
    public async Task Availability_first_makes_later_ordinary_confirmation_reject_unavailable()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Unavailable race", "5", token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, token);
        using var interventionClient = await fixture.LoginAsync(actor, token);
        using var unavailable = await PostAvailabilityAsync(
            interventionClient, product.Id, true, false, token);
        unavailable.EnsureSuccessStatusCode();

        using var confirmation = await PostConfirmationAsync(
            fixture.OrderOperationsClient, product.Id, token);
        await DeliveryQuantityTestSupport.AssertProblemAsync(
            confirmation,
            HttpStatusCode.Conflict,
            "order_operations.first_confirmation.product_unavailable",
            token);
        Assert.Empty(await fixture.ReadConfirmedContentsAsync(token));
    }

    private static async Task<HttpResponseMessage> PostConfirmationAsync(HttpClient client, Guid productId, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
        { Content = JsonContent.Create(new FirstConfirmationRequest("Concurrency", [new FirstConfirmationItemRequest(productId, 1)])) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, token);
    }

    private static async Task<HttpResponseMessage> PostRetireAsync(HttpClient client, Guid productId, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/catalog/products/{productId:D}/retire");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, token);
    }

    private static async Task<HttpResponseMessage> PostAvailabilityAsync(
        HttpClient client,
        Guid productId,
        bool expected,
        bool next,
        CancellationToken token)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/catalog/products/{productId:D}/availability-changes")
        {
            Content = JsonContent.Create(new
            {
                expectedCurrentAvailability = expected,
                newAvailability = next
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            client, request, token);
    }

    private sealed class HoldingCatalogSnapshot(
        IOrderConfirmationCatalog inner, TaskCompletionSource reached, TaskCompletionSource release) : IOrderConfirmationCatalog
    {
        public async Task<IReadOnlyList<OrderConfirmationCatalogProduct>> ReadProductsAsync(IReadOnlyCollection<Guid> ids, DbTransaction transaction, CancellationToken token)
        {
            var products = await inner.ReadProductsAsync(ids, transaction, token);
            reached.TrySetResult();
            await release.Task.WaitAsync(token);
            return products;
        }
    }
}
