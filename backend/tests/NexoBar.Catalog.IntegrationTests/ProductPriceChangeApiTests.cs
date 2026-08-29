using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexoBar.Catalog;

namespace NexoBar.Catalog.IntegrationTests;

[Collection(CatalogApiCollection.Name)]
public sealed class ProductPriceChangeApiTests(CatalogApiFixture fixture)
{
    [Fact]
    public async Task Changes_current_price_from_10_to_12()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10.00", cancellationToken);

        using var response = await PostPriceChangeAsync(
            product.Id, "10.00", "12.00", NewIdempotencyKey(), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new ProductPriceResponse(product.Id, "12.00"),
            await response.Content.ReadFromJsonAsync<ProductPriceResponse>(cancellationToken));
        Assert.Equal((12.00m, 1), await fixture.ReadPriceChangeStateAsync(
            product.Id, cancellationToken));
    }

    [Fact]
    public async Task Zero_is_a_valid_new_price()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);

        using var response = await PostPriceChangeAsync(
            product.Id, "10", "0", NewIdempotencyKey(), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((0m, 1), await fixture.ReadPriceChangeStateAsync(product.Id, cancellationToken));
    }

    [Fact]
    public async Task Equal_expected_and_new_price_succeeds_and_persists_command()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);

        using var response = await PostPriceChangeAsync(
            product.Id, "10.0", "10.00", NewIdempotencyKey(), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((10m, 1), await fixture.ReadPriceChangeStateAsync(product.Id, cancellationToken));
    }

    [Fact]
    public async Task Negative_new_price_is_rejected_without_effect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);

        using var response = await PostPriceChangeAsync(
            product.Id, "10", "-0.01", NewIdempotencyKey(), cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(
            response,
            "catalog.product.price_change_invalid",
            cancellationToken,
            "newPrice");
        Assert.Equal((10m, 0), await fixture.ReadPriceChangeStateAsync(product.Id, cancellationToken));
    }

    [Fact]
    public async Task Different_expected_price_reports_current_price_without_effect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10.00", cancellationToken);

        using var response = await PostPriceChangeAsync(
            product.Id, "9", "12", NewIdempotencyKey(), cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = await ReadProblemAsync(response, cancellationToken);
        Assert.Equal(
            "catalog.product.price_concurrency_conflict",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(product.Id, problem.RootElement.GetProperty("productId").GetGuid());
        Assert.Equal("10.00", problem.RootElement.GetProperty("currentPrice").GetString());
        Assert.Equal((10m, 0), await fixture.ReadPriceChangeStateAsync(product.Id, cancellationToken));
    }

    [Fact]
    public async Task Missing_product_returns_not_found_without_reserving_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);

        using var response = await PostPriceChangeAsync(
            Guid.NewGuid(), "10", "12", NewIdempotencyKey(), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertProblemAsync(response, "catalog.product.not_found", cancellationToken);
    }

    [Fact]
    public async Task Physically_existing_inactive_product_returns_not_current()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        await fixture.SetProductActiveAsync(product.Id, false, cancellationToken);

        using var response = await PostPriceChangeAsync(
            product.Id, "10", "12", NewIdempotencyKey(), cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemAsync(response, "catalog.product.not_current", cancellationToken);
        Assert.Equal((10m, 0), await fixture.ReadPriceChangeStateAsync(product.Id, cancellationToken));
    }

    [Fact]
    public async Task Same_key_and_numerically_equal_intent_replays_identical_durable_result()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var key = NewIdempotencyKey();

        using var first = await PostPriceChangeAsync(
            product.Id, "10.0", "12.00", key, cancellationToken);
        using var replay = await PostPriceChangeAsync(
            product.Id, "10.00", "12.0", key, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(cancellationToken),
            await replay.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal((12m, 1), await fixture.ReadPriceChangeStateAsync(product.Id, cancellationToken));
    }

    [Fact]
    public async Task Same_key_and_different_intent_conflicts_without_second_effect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var key = NewIdempotencyKey();

        using var first = await PostPriceChangeAsync(
            product.Id, "10", "12", key, cancellationToken);
        using var conflict = await PostPriceChangeAsync(
            product.Id, "12", "14", key, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await AssertProblemAsync(
            conflict, "catalog.product.idempotency_key_conflict", cancellationToken);
        Assert.Equal((12m, 1), await fixture.ReadPriceChangeStateAsync(product.Id, cancellationToken));
    }

    [Fact]
    public async Task Concurrent_same_key_requests_replay_one_effect_and_one_command()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var key = NewIdempotencyKey();

        var responses = await Task.WhenAll(
            PostPriceChangeAsync(product.Id, "10", "12", key, cancellationToken),
            PostPriceChangeAsync(product.Id, "10", "12", key, cancellationToken));

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(
                await responses[0].Content.ReadAsStringAsync(cancellationToken),
                await responses[1].Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal((12m, 1), await fixture.ReadPriceChangeStateAsync(
                product.Id, cancellationToken));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Different_keys_with_same_expected_price_allow_exactly_one_update()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);

        var responses = await Task.WhenAll(
            PostPriceChangeAsync(
                product.Id, "10", "12", NewIdempotencyKey(), cancellationToken),
            PostPriceChangeAsync(
                product.Id, "10", "14", NewIdempotencyKey(), cancellationToken));

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var conflict = Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.Conflict);
            await AssertProblemAsync(
                conflict, "catalog.product.price_concurrency_conflict", cancellationToken);
            var state = await fixture.ReadPriceChangeStateAsync(product.Id, cancellationToken);
            Assert.Contains(state.Price, new[] { 12m, 14m });
            Assert.Equal(1, state.Commands);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Replay_after_host_restart_returns_original_durable_result()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var key = NewIdempotencyKey();

        using var first = await PostPriceChangeAsync(
            product.Id, "10", "12.00", key, cancellationToken);
        var originalBody = await first.Content.ReadAsStringAsync(cancellationToken);
        await fixture.RestartApplicationAsync(cancellationToken);
        using var replay = await PostPriceChangeAsync(
            product.Id, "10.0", "12.0", key, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync(cancellationToken));
    }

    [Fact]
    public async Task Replay_returns_original_result_after_a_later_price_change()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var firstKey = NewIdempotencyKey();

        using var first = await PostPriceChangeAsync(
            product.Id, "10", "12", firstKey, cancellationToken);
        using var later = await PostPriceChangeAsync(
            product.Id, "12", "15", NewIdempotencyKey(), cancellationToken);
        using var replay = await PostPriceChangeAsync(
            product.Id, "10", "12", firstKey, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(cancellationToken),
            await replay.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal((15m, 2), await fixture.ReadPriceChangeStateAsync(product.Id, cancellationToken));
    }

    [Fact]
    public async Task Command_insert_failure_rolls_back_product_price()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        await fixture.SetPriceChangeCommandFailureAsync(true, cancellationToken);

        try
        {
            using var response = await PostPriceChangeAsync(
                product.Id, "10", "12", NewIdempotencyKey(), cancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal((10m, 0), await fixture.ReadPriceChangeStateAsync(
                product.Id, cancellationToken));
        }
        finally
        {
            await fixture.SetPriceChangeCommandFailureAsync(false, cancellationToken);
        }
    }

    [Fact]
    public async Task Unknown_json_property_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);

        using var response = await PostPriceChangeAsync(
            product.Id,
            new { expectedCurrentPrice = "10", newPrice = "12", name = "No" },
            NewIdempotencyKey(),
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((10m, 0), await fixture.ReadPriceChangeStateAsync(product.Id, cancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    [InlineData("0198f56e-44dc-7eab-93d7-58949903cb2a")]
    public async Task Idempotency_key_is_required_and_must_be_uuid_v4(string? key)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);

        using var response = await PostPriceChangeAsync(
            product.Id, "10", "12", key, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(
            response,
            key is null
                ? "catalog.product.idempotency_key_required"
                : "catalog.product.idempotency_key_invalid",
            cancellationToken);
        Assert.Equal((10m, 0), await fixture.ReadPriceChangeStateAsync(product.Id, cancellationToken));
    }

    [Fact]
    public async Task OpenApi_describes_price_change_contract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await fixture.Client.GetAsync("/openapi/v1.json", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/catalog/products/{productId}/price-changes")
            .GetProperty("post");
        var header = operation.GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.True(header.GetProperty("required").GetBoolean());
        foreach (var status in new[] { "200", "400", "404", "409" })
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var requestSchema = schemas.GetProperty(nameof(ChangeProductPriceRequest));
        var required = requestSchema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()).ToArray();
        Assert.Contains("expectedCurrentPrice", required);
        Assert.Contains("newPrice", required);
        Assert.Equal(
            "string",
            requestSchema.GetProperty("properties").GetProperty("expectedCurrentPrice")
                .GetProperty("type").GetString());
        Assert.Equal(
            "string",
            requestSchema.GetProperty("properties").GetProperty("newPrice")
                .GetProperty("type").GetString());
        Assert.Equal(
            "string",
            schemas.GetProperty(nameof(ProductPriceResponse)).GetProperty("properties")
                .GetProperty("price").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Catalog_model_has_no_pending_changes()
    {
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    private Task<HttpResponseMessage> PostPriceChangeAsync(
        Guid productId,
        string expectedCurrentPrice,
        string newPrice,
        string? key,
        CancellationToken cancellationToken) =>
        PostPriceChangeAsync(
            productId,
            new ChangeProductPriceRequest(expectedCurrentPrice, newPrice),
            key,
            cancellationToken);

    private async Task<HttpResponseMessage> PostPriceChangeAsync(
        Guid productId,
        object request,
        string? key,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/catalog/products/{productId}/price-changes")
        {
            Content = JsonContent.Create(request, request.GetType())
        };
        if (key is not null)
        {
            message.Headers.Add("Idempotency-Key", key);
        }

        return await fixture.Client.SendAsync(message, cancellationToken);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        string code,
        CancellationToken cancellationToken,
        string? field = null)
    {
        using var problem = await ReadProblemAsync(response, cancellationToken);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        if (field is not null)
        {
            Assert.Equal(field, problem.RootElement.GetProperty("field").GetString());
        }
    }

    private static async Task<JsonDocument> ReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

    private static string NewIdempotencyKey() => Guid.NewGuid().ToString("D");
}
