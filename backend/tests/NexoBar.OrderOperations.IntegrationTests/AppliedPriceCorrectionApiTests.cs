using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed partial class AppliedPriceCorrectionApiTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Adopts_current_catalog_price_preserves_confirmation_and_reprices_effective_delivery()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, token);
        var content = Assert.Single(await fixture.ReadConfirmedContentsAsync(token));
        await SetCatalogPriceAsync(content.ProductId, 8m, token);

        using var first = await PostAsync(target, Guid.NewGuid(), token);
        var firstResult = await ReadSuccessAsync(first, token);
        Assert.Equal("5", firstResult.PreviousEffectiveAppliedPrice);
        Assert.Equal("8", firstResult.ResultingEffectiveAppliedPrice);
        await AssertPriceStateAsync(content.IncorporationId, content.ContentOrdinal, 8m, token);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            Assert.Equal(5m, (await db.IncorporationContents.SingleAsync(x => x.IncorporationId == content.IncorporationId && x.ContentOrdinal == content.ContentOrdinal, token)).AppliedPrice);
        }

        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 2, token);
        Assert.Equal(16m, decimal.Parse((await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, token)).FunctionalAmount, System.Globalization.CultureInfo.InvariantCulture));
        await SetCatalogPriceAsync(content.ProductId, 9m, token);
        using var second = await PostAsync(target, Guid.NewGuid(), token);
        await ReadSuccessAsync(second, token);
        Assert.Equal(18m, decimal.Parse((await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, token)).FunctionalAmount, System.Globalization.CultureInfo.InvariantCulture));
        await using var finalScope = fixture.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Equal(2, await finalDb.AppliedPriceCorrectionHistory.CountAsync(token));
        Assert.Equal(2, await finalDb.AppliedPriceCorrectionCommands.CountAsync(token));
    }

    [Fact]
    public async Task New_key_with_current_effective_catalog_price_rejects_without_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 1, token);
        using var response = await PostAsync(target, Guid.NewGuid(), token);
        await DeliveryQuantityTestSupport.AssertProblemAsync(response, HttpStatusCode.Conflict, "order_operations.applied_price_correction.no_correction_to_apply", token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Empty(await db.AppliedPriceCorrectionHistory.ToArrayAsync(token));
        Assert.Empty(await db.AppliedPriceCorrectionCommands.ToArrayAsync(token));
    }

    private async Task<HttpResponseMessage> PostAsync(DeliveryTarget target, Guid key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/order-operations/orders/{target.OperationalReference}/incorporations/{target.IncorporationId}/contents/{target.ContentOrdinal}/apply-current-catalog-price")
        { Content = JsonContent.Create(new ApplyCurrentCatalogPriceRequest()) };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, token);
    }

    private static async Task<AppliedPriceCorrectionResponse> ReadSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<AppliedPriceCorrectionResponse>(await response.Content.ReadFromJsonAsync<AppliedPriceCorrectionResponse>(token));
    }

    private async Task SetCatalogPriceAsync(Guid productId, decimal price, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE catalog.products SET price = @price WHERE id = @id";
        command.Parameters.AddWithValue("price", price);
        command.Parameters.AddWithValue("id", productId);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task AssertPriceStateAsync(Guid incorporationId, int contentOrdinal, decimal price, CancellationToken token)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Equal(price, (await db.ContentAppliedPriceStates.SingleAsync(x => x.IncorporationId == incorporationId && x.ContentOrdinal == contentOrdinal, token)).EffectiveAppliedPrice);
    }
}
