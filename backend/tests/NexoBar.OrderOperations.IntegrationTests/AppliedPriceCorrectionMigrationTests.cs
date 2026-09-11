using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class AppliedPriceCorrectionMigrationTests(OrderOperationsApiFixture fixture)
{
    private const string Previous = "20260910222430_AddCompleteOrderCancellation";
    private const string Current = "20260911160042_AddAppliedPriceCorrection";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Backfill_copies_exact_original_prices_without_history_and_uncorrected_down_is_safe()
    {
        await fixture.ResetAsync(Token);
        await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("10.1234", 2), ("3", 1)], Token);
        try
        {
            await fixture.MigrateOrderOperationsAsync(Previous, Token);
            await fixture.MigrateOrderOperationsAsync(Current, Token);
            await using (var scope = fixture.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
                var contents = await db.IncorporationContents.AsNoTracking().ToArrayAsync(Token);
                var prices = await db.ContentAppliedPriceStates.AsNoTracking().ToArrayAsync(Token);
                Assert.Equal(2, prices.Length);
                Assert.All(contents, content => Assert.Equal(content.AppliedPrice,
                    Assert.Single(prices, state => state.IncorporationId == content.IncorporationId &&
                        state.ContentOrdinal == content.ContentOrdinal).EffectiveAppliedPrice));
                Assert.Empty(await db.AppliedPriceCorrectionHistory.ToArrayAsync(Token));
                Assert.Empty(await db.AppliedPriceCorrectionCommands.ToArrayAsync(Token));
            }
            await fixture.MigrateOrderOperationsAsync(Previous, Token);
            await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT to_regclass('order_operations.content_applied_price_states') IS NULL";
            Assert.True((bool)(await command.ExecuteScalarAsync(Token))!);
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync(Current, Token);
            await fixture.ResetAsync(Token);
        }
    }

    [Theory]
    [InlineData("effective-state")]
    [InlineData("history")]
    [InlineData("durable-result")]
    public async Task Down_refuses_loss_of_corrected_state_or_history_even_after_return_to_original(string meaning)
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 1, Token);
        try
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            if (meaning == "effective-state")
            {
                // Isolate the state guard, without relying on the History/command guards.
                await db.Database.ExecuteSqlRawAsync("UPDATE order_operations.content_applied_price_states SET effective_applied_price = 8", Token);
            }
            else
            {
                var product = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token)).ProductId;
                foreach (var price in new[] { 8m, 5m })
                {
                    await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, Token);
                    await using var change = connection.CreateCommand();
                    change.CommandText = "UPDATE catalog.products SET price = @price WHERE id = @id";
                    change.Parameters.AddWithValue("price", price);
                    change.Parameters.AddWithValue("id", product);
                    await change.ExecuteNonQueryAsync(Token);
                    using var request = new HttpRequestMessage(HttpMethod.Post,
                        $"/api/order-operations/orders/{target.OperationalReference}/incorporations/{target.IncorporationId}/contents/{target.ContentOrdinal}/apply-current-catalog-price")
                    { Content = JsonContent.Create(new ApplyCurrentCatalogPriceRequest()) };
                    request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
                    using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, Token);
                    response.EnsureSuccessStatusCode();
                }
                Assert.Equal(5m, (await db.ContentAppliedPriceStates.AsNoTracking().SingleAsync(Token)).EffectiveAppliedPrice);
                if (meaning == "history") await db.AppliedPriceCorrectionCommands.ExecuteDeleteAsync(Token);
            }
            var error = await Assert.ThrowsAsync<PostgresException>(() => fixture.MigrateOrderOperationsAsync(Previous, Token));
            Assert.Contains("Cannot remove meaningful Applied Price Correction", error.MessageText);
            Assert.Single(await db.ContentAppliedPriceStates.AsNoTracking().ToArrayAsync(Token));
            Assert.Equal(meaning == "effective-state" ? 8m : 5m,
                (await db.ContentAppliedPriceStates.AsNoTracking().SingleAsync(Token)).EffectiveAppliedPrice);
            Assert.Equal(meaning == "effective-state" ? 0 : 2, await db.AppliedPriceCorrectionHistory.CountAsync(Token));
            Assert.Equal(meaning == "durable-result" ? 2 : 0, await db.AppliedPriceCorrectionCommands.CountAsync(Token));
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync(Current, Token);
            await fixture.ResetAsync(Token);
        }
    }
}
