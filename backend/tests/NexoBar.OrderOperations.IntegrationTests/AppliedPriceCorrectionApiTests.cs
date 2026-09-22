using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
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
        Assert.Equal(16m, decimal.Parse((await LiquidationTestSupport.ReadOrderAsync(fixture.OrderOperationsClient, target.OperationalReference, token)).FunctionalAmount, System.Globalization.CultureInfo.InvariantCulture));
        await SetCatalogPriceAsync(content.ProductId, 9m, token);
        using var second = await PostAsync(target, Guid.NewGuid(), token);
        await ReadSuccessAsync(second, token);
        Assert.Equal(18m, decimal.Parse((await LiquidationTestSupport.ReadOrderAsync(fixture.OrderOperationsClient, target.OperationalReference, token)).FunctionalAmount, System.Globalization.CultureInfo.InvariantCulture));
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

    [Fact]
    public async Task Retired_product_keeps_confirmed_price_and_can_supply_a_corrected_configured_price()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 1, token);
        var content = Assert.Single(await fixture.ReadConfirmedContentsAsync(token));

        using (var retire = await PostCatalogAsync(
            $"/api/catalog/products/{content.ProductId:D}/retire", null, token))
        {
            retire.EnsureSuccessStatusCode();
        }
        await AssertAppliedAndCatalogStateAsync(content, 5m, 5m, false, token);

        using (var change = await PostCatalogAsync(
            $"/api/catalog/products/{content.ProductId:D}/price-changes",
            new ChangeProductPriceRequest("5", "8"), token))
        {
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        }
        await AssertAppliedAndCatalogStateAsync(content, 5m, 8m, false, token);

        using (var browse = await fixture.OrderOperationsClient.GetAsync(
            "/api/catalog/operational-products", token))
        {
            browse.EnsureSuccessStatusCode();
            var products = await browse.Content.ReadFromJsonAsync<OperationalProductResponse[]>(token);
            Assert.DoesNotContain(products!, product => product.Id == content.ProductId);
        }

        using (var confirmation = await PostFirstConfirmationAsync(content.ProductId, token))
        {
            await DeliveryQuantityTestSupport.AssertProblemAsync(
                confirmation, HttpStatusCode.Conflict,
                "order_operations.confirmation.product_not_current", token);
        }

        using var correction = await PostAsync(target, Guid.NewGuid(), token);
        var result = await ReadSuccessAsync(correction, token);
        Assert.Equal("5", result.PreviousEffectiveAppliedPrice);
        Assert.Equal("8", result.ResultingEffectiveAppliedPrice);
        await AssertAppliedAndCatalogStateAsync(content, 8m, 8m, false, token);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var history = Assert.Single(await db.AppliedPriceCorrectionHistory.ToArrayAsync(token));
        Assert.Equal(5m, history.PreviousEffectiveAppliedPrice);
        Assert.Equal(8m, history.ResultingEffectiveAppliedPrice);
    }

    [Fact]
    public async Task Retiring_a_prepared_product_preserves_existing_work_and_its_progress()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 2, 0, token);
        var content = Assert.Single(await fixture.ReadConfirmedContentsAsync(token));
        var before = Assert.Single(await fixture.ReadPreparationWorkAsync(token));

        using (var retire = await PostCatalogAsync(
            $"/api/catalog/products/{content.ProductId:D}/retire", null, token))
        {
            retire.EnsureSuccessStatusCode();
        }

        var actor = await fixture.CreatePreparationActorAsync(
            true, before.PreparationResponsibilityId, token);
        using var client = await fixture.LoginAsync(actor, token);
        using var start = await PreparationStartTestSupport.PostAsync(
            client, before.Id, Guid.NewGuid(), 1, token);
        await PreparationStartTestSupport.ReadSuccessAsync(start, token);

        var after = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.PreparationResponsibilityId, after.PreparationResponsibilityId);
        Assert.Equal(2, after.TotalQuantity);
        Assert.Equal(1, after.PendingQuantity);
        Assert.Equal(1, after.InPreparationQuantity);
        Assert.Equal(0, after.ReadyQuantity);
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

    private async Task<HttpResponseMessage> PostCatalogAsync(
        string path, object? body, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient, request, token);
    }

    private async Task<HttpResponseMessage> PostFirstConfirmationAsync(
        Guid productId, CancellationToken token)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                "Retired product", [new FirstConfirmationItemRequest(productId, 1)]))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient, request, token);
    }

    private async Task AssertAppliedAndCatalogStateAsync(
        ConfirmedContentSnapshot content, decimal effectivePrice, decimal catalogPrice,
        bool isActive, CancellationToken token)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(effectivePrice, (await orders.ContentAppliedPriceStates.SingleAsync(
            x => x.IncorporationId == content.IncorporationId &&
                 x.ContentOrdinal == content.ContentOrdinal, token)).EffectiveAppliedPrice);
        Assert.Equal(5m, (await orders.IncorporationContents.SingleAsync(
            x => x.IncorporationId == content.IncorporationId &&
                 x.ContentOrdinal == content.ContentOrdinal, token)).AppliedPrice);
        var product = await catalog.Products.SingleAsync(x => x.Id == content.ProductId, token);
        Assert.Equal(catalogPrice, product.Price);
        Assert.Equal(isActive, product.IsActive);
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
