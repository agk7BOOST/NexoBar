using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class UnavailableProductExceptionMigrationTests(
    OrderOperationsApiFixture fixture)
{
    private const string Previous = "20260911160042_AddAppliedPriceCorrection";
    private const string Current = "20260921120000_AddUnavailableProductException";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrderOperations_model_has_no_pending_changes()
    {
        await fixture.ResetAsync(Token);
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    [Fact]
    public async Task Migration_defaults_historical_intent_and_applied_markers_to_false_and_is_reversible()
    {
        await fixture.ResetAsync(Token);
        var product = await fixture.CreateProductAsync("Historical", "10", Token);
        using var first = await PostFirstAsync(product.Id, Guid.NewGuid());
        var order = Assert.IsType<FirstConfirmationResponse>(
            await first.Content.ReadFromJsonAsync<FirstConfirmationResponse>(Token));
        var pending = await fixture.StartPendingCompositionAsync(order.OperationalReference, Token);
        using var subsequent = await PostSubsequentAsync(
            order.OperationalReference,
            new SubsequentConfirmationRequest(pending.PendingCompositionId,
                [new(product.Id, 1)]),
            Guid.NewGuid());
        subsequent.EnsureSuccessStatusCode();

        try
        {
            await fixture.MigrateOrderOperationsAsync(Previous, Token);
            await AssertColumnsAsync(exist: false);
            await fixture.MigrateOrderOperationsAsync(Current, Token);
            await AssertColumnsAsync(exist: true);

            await using var scope = fixture.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            Assert.All(await dbContext.IncorporationContents.AsNoTracking().ToArrayAsync(Token),
                content => Assert.False(content.UnavailableProductExceptionApplied));
            Assert.All(await dbContext.FirstConfirmationCommandContents.AsNoTracking()
                    .ToArrayAsync(Token),
                content => Assert.False(content.IntentUnavailableProductExceptionRequested));
            Assert.All(await dbContext.SubsequentConfirmationCommandContents.AsNoTracking()
                    .ToArrayAsync(Token),
                content => Assert.False(content.IntentUnavailableProductExceptionRequested));
            Assert.False(await fixture.HasPendingModelChangesAsync());

            await fixture.MigrateOrderOperationsAsync(Previous, Token);
            await AssertColumnsAsync(exist: false);
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync(Current, Token);
            await fixture.ResetAsync(Token);
        }
    }

    [Fact]
    public async Task Down_refuses_to_discard_meaningful_exception_history()
    {
        await fixture.ResetAsync(Token);
        var product = await fixture.CreateProductAsync("Unavailable", "10", Token);
        await fixture.SetProductStateAsync(product.Id, true, false, Token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var client = await fixture.LoginAsync(actor, Token);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                "Migration", [new(product.Id, 1, null, true)]))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            client, request, Token);
        response.EnsureSuccessStatusCode();

        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                fixture.MigrateOrderOperationsAsync(Previous, Token));
            Assert.Contains("Cannot remove meaningful unavailable Product exception",
                error.MessageText);
            await AssertColumnsAsync(exist: true);
        }
        finally
        {
            await fixture.ResetAsync(Token);
        }
    }

    private async Task AssertColumnsAsync(bool exist)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*)
            FROM information_schema.columns
            WHERE table_schema = 'order_operations'
              AND ((table_name = 'incorporation_contents'
                    AND column_name = 'unavailable_product_exception_applied')
                OR (table_name IN ('first_confirmation_command_contents',
                                   'subsequent_confirmation_command_contents')
                    AND column_name = 'intent_unavailable_product_exception_requested'))
            """;
        Assert.Equal(exist ? 3L : 0L,
            Assert.IsType<long>(await command.ExecuteScalarAsync(Token)));
    }

    private async Task<HttpResponseMessage> PostFirstAsync(Guid productId, Guid key)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                "Migration", [new(productId, 1)]))
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient, request, Token);
    }

    private async Task<HttpResponseMessage> PostSubsequentAsync(
        string orderId,
        SubsequentConfirmationRequest body,
        Guid key)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{orderId}/confirmations")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient, request, Token);
    }
}
