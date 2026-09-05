using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class OrderDeliveryAuthorizationConcurrencyTests(
    OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Authorized_read_locks_responsibility_until_its_snapshot_completes()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "3", token);
        using var confirmationRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = System.Net.Http.Json.JsonContent.Create(
                new FirstConfirmationRequest(
                    "Mesa concurrente",
                    [new FirstConfirmationItemRequest(product.Id, 1)]))
        };
        confirmationRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var confirmationResponse =
            await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
                fixture.OrderOperationsClient,
                confirmationRequest,
                token);
        confirmationResponse.EnsureSuccessStatusCode();
        var confirmation = Assert.IsType<FirstConfirmationResponse>(
            await confirmationResponse.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
        var lookupReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLookup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithProductLookupDecorator(
            services => new BlockingProductOperationalReferenceLookup(
                new ProductOperationalReferenceLookup(
                    services.GetRequiredService<CatalogDbContext>()),
                lookupReached,
                releaseLookup));
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token, application);

        var readTask = client.GetAsync(
            $"/api/order-operations/orders/{confirmation.OperationalReference}/delivery",
            token);
        await lookupReached.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var revokeTask = fixture.RevokeOrderOperationsAssignmentAsync(actor.IdentityId, token);
        try
        {
            Assert.True(await fixture.WaitForIdentityMutationLockAsync(
                "responsibility_assignments",
                TimeSpan.FromSeconds(10),
                token));
            Assert.False(revokeTask.IsCompleted);

            releaseLookup.TrySetResult();
            using var response = await readTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await revokeTask;

            using var afterRevocation = await client.GetAsync(
                $"/api/order-operations/orders/{confirmation.OperationalReference}/delivery",
                token);
            Assert.Equal(HttpStatusCode.Forbidden, afterRevocation.StatusCode);
        }
        finally
        {
            releaseLookup.TrySetResult();
            await ObserveAsync(readTask);
            await ObserveAsync(revokeTask);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The original assertion preserves the relevant failure.
        }
    }
}
