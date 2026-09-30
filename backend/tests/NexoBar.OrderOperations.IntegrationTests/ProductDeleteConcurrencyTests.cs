using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.OrderOperations;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ProductDeleteConcurrencyTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Confirmation_commits_first_and_Delete_rejects_confirmed_participation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Race confirmation first", "5", token);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithCatalogDecorator(services =>
            new HoldingCatalog(new OrderConfirmationCatalog(services.GetRequiredService<CatalogDbContext>(), services.GetRequiredService<NexoBar.OperationalConfiguration.IPreparationResponsibilityLookup>()), reached, release));
        using var client = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, token, application);

        var confirmationTask = ConfirmAsync(client, product.Id, token);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var deleteTask = DeleteAsync(client, product.Id, token);
            Assert.True(await WaitForProductLockAsync(TimeSpan.FromSeconds(10), token));
            release.TrySetResult();
            using var confirmed = await confirmationTask.WaitAsync(TimeSpan.FromSeconds(10), token);
            using var deleted = await deleteTask.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
            Assert.Single(await fixture.ReadConfirmedContentsAsync(token));
            Assert.Equal(product.Id, (await fixture.ReadProductAsync(product.Id, token)).Id);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Delete_commits_first_and_Confirmation_rejects_missing_Product()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Race delete first", "5", token);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithParticipationDecorator(_ =>
            new HoldingParticipation(reached, release));
        using var client = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, token, application);

        var deleteTask = DeleteAsync(client, product.Id, token);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var confirmationTask = ConfirmAsync(client, product.Id, token);
            Assert.True(await WaitForProductLockAsync(TimeSpan.FromSeconds(10), token));
            release.TrySetResult();
            using var deleted = await deleteTask.WaitAsync(TimeSpan.FromSeconds(10), token);
            using var confirmation = await confirmationTask.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, confirmation.StatusCode);
            Assert.Empty(await fixture.ReadConfirmedContentsAsync(token));
        }
        finally { release.TrySetResult(); }
    }

    private async Task<bool> WaitForProductLockAsync(TimeSpan timeout, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock' AND query LIKE '%catalog.products%')
                """;
            if ((bool)(await command.ExecuteScalarAsync(token))!) return true;
            await Task.Delay(20, token);
        }
        return false;
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid productId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/catalog/products/{productId}");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, token);
    }

    private static async Task<HttpResponseMessage> ConfirmAsync(HttpClient client, Guid productId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
        { Content = JsonContent.Create(new FirstConfirmationRequest("Race", [new FirstConfirmationItemRequest(productId, 1)])) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, token);
    }

    private sealed class HoldingCatalog(IOrderConfirmationCatalog inner, TaskCompletionSource reached,
        TaskCompletionSource release) : IOrderConfirmationCatalog
    {
        public async Task<IReadOnlyList<OrderConfirmationCatalogProduct>> ReadProductsAsync(
            IReadOnlyCollection<Guid> ids, DbTransaction transaction, CancellationToken token)
        {
            var products = await inner.ReadProductsAsync(ids, transaction, token);
            reached.TrySetResult();
            await release.Task.WaitAsync(token);
            return products;
        }
    }

    private sealed class HoldingParticipation(TaskCompletionSource reached, TaskCompletionSource release)
        : IConfirmedProductParticipation
    {
        public async Task<bool> HasConfirmedParticipationAsync(Guid productId,
            DbTransaction transaction, CancellationToken token)
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(token);
            return false;
        }
    }
}
