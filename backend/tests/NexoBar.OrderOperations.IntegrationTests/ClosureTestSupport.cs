using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

internal static class ClosureTestSupport
{
    internal static async Task<FirstConfirmationResponse> CreateOrderAsync(
        OrderOperationsApiFixture fixture, CancellationToken token, bool liquidate = true, bool external = true)
    {
        var order = await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("5", 2)], token);
        await fixture.SetAllDeliveredQuantitiesAsync(Guid.Parse(order.OperationalReference), token);
        if (liquidate)
        {
            using var response = external
                ? await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, order.OperationalReference, Guid.NewGuid(), token)
                : await LiquidationTestSupport.PostSimpleAsync(fixture.OrderOperationsClient, order.OperationalReference, Guid.NewGuid(), "Cash", token);
            await LiquidationTestSupport.ReadSuccessAsync(response, token);
        }
        return order;
    }

    internal static async Task<HttpResponseMessage> PostAsync(HttpClient client, string orderId, Guid key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{orderId}/close");
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, token);
    }

    internal static async Task<ClosureResponse> ReadSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<ClosureResponse>(await response.Content.ReadFromJsonAsync<ClosureResponse>(token));
    }

    internal static Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, CancellationToken token) =>
        LiquidationTestSupport.AssertProblemAsync(response, status, $"order_operations.closure.{code}", token);

    internal static async Task AssertCountsAsync(OrderOperationsApiFixture fixture, int count, CancellationToken token)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Equal(count, await db.Closures.CountAsync(token));
        Assert.Equal(count, await db.ClosureHistory.CountAsync(token));
        Assert.Equal(count, await db.ClosureCommands.CountAsync(token));
    }

    internal static async Task<NpgsqlConnection> OpenConnectionAsync(OrderOperationsApiFixture fixture, CancellationToken token)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        return connection;
    }

    internal static async Task<NpgsqlTransaction> LockOrderAsync(NpgsqlConnection connection, string orderId, CancellationToken token)
    {
        var transaction = await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM order_operations.orders WHERE id = @id FOR UPDATE";
        command.Parameters.AddWithValue("id", Guid.Parse(orderId));
        await command.ExecuteScalarAsync(token);
        return transaction;
    }
}
