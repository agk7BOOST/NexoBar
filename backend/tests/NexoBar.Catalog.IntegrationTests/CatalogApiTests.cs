using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexoBar.Catalog;

namespace NexoBar.Catalog.IntegrationTests;

[Collection(CatalogApiCollection.Name)]
public sealed class CatalogApiTests(CatalogApiFixture fixture)
{
    [Fact]
    public async Task Backend_creates_and_reads_an_exact_product_without_frontend()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var request = new CreateProductRequest(
            "Agua Tónica Élite",
            "12345678901234567890.12345678",
            false);

        using var createResponse = await PostProductAsync(
            request,
            NewIdempotencyKey(),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ProductResponse>(
            cancellationToken);
        Assert.NotNull(created);
        Assert.Equal(7, created.Id.ToByteArray(bigEndian: true)[6] >> 4);
        Assert.Equal(request.OperationalName, created.OperationalName);
        Assert.Equal(request.Price, created.Price);
        Assert.True(created.IsActive);
        Assert.True(created.IsAvailable);
        Assert.False(created.RequiresPreparation);

        using var getResponse = await fixture.Client.GetAsync(
            $"/api/catalog/products/{created.Id}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(
            created,
            await getResponse.Content.ReadFromJsonAsync<ProductResponse>(cancellationToken));

        var listed = await ListProductsAsync(cancellationToken);
        Assert.Equal(created, Assert.Single(listed));
    }

    [Fact]
    public async Task Missing_idempotency_key_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);

        using var response = await PostProductAsync(
            new CreateProductRequest("Agua", "10", false),
            idempotencyKey: null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ListProductsAsync(cancellationToken));
    }

    [Fact]
    public async Task Idempotency_key_must_be_a_uuid_v4()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var request = new CreateProductRequest("Agua", "10", false);

        using var malformedResponse = await PostProductAsync(
            request,
            "not-a-uuid",
            cancellationToken);
        using var versionSevenResponse = await PostProductAsync(
            request,
            Guid.CreateVersion7().ToString("D"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, versionSevenResponse.StatusCode);
        Assert.Empty(await ListProductsAsync(cancellationToken));
    }

    [Fact]
    public async Task Zero_price_is_accepted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);

        using var response = await PostProductAsync(
            new CreateProductRequest("Producto sin cargo", "0", false),
            NewIdempotencyKey(),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(
            cancellationToken);
        Assert.NotNull(product);
        Assert.Equal("0", product.Price);
        Assert.Equal(product, Assert.Single(await ListProductsAsync(cancellationToken)));
    }

    [Fact]
    public async Task Negative_price_is_rejected_as_a_functional_error()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);

        using var response = await PostProductAsync(
            new CreateProductRequest("Producto inválido", "-0.01", false),
            NewIdempotencyKey(),
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using var problem = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal("catalog.product.invalid", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("price", problem.RootElement.GetProperty("field").GetString());
        Assert.Equal(
            "Price must be greater than or equal to zero.",
            problem.RootElement.GetProperty("detail").GetString());
        Assert.Empty(await ListProductsAsync(cancellationToken));
    }

    [Fact]
    public async Task Same_key_and_same_intention_replays_the_same_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var key = NewIdempotencyKey();
        var request = new CreateProductRequest("Soda", "10.00", false);

        using var firstResponse = await PostProductAsync(request, key, cancellationToken);
        using var replayResponse = await PostProductAsync(request, key, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(firstResponse.StatusCode, replayResponse.StatusCode);
        Assert.Equal(firstResponse.Headers.Location, replayResponse.Headers.Location);
        var firstProduct = await firstResponse.Content.ReadFromJsonAsync<ProductResponse>(
            cancellationToken);
        var replayedProduct = await replayResponse.Content.ReadFromJsonAsync<ProductResponse>(
            cancellationToken);
        Assert.Equal(firstProduct, replayedProduct);
        Assert.Equal(firstProduct, Assert.Single(await ListProductsAsync(cancellationToken)));
    }

    [Fact]
    public async Task Same_key_and_different_intention_conflicts_without_a_second_effect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var key = NewIdempotencyKey();

        using var firstResponse = await PostProductAsync(
            new CreateProductRequest("Soda", "10", false),
            key,
            cancellationToken);
        using var incompatibleResponse = await PostProductAsync(
            new CreateProductRequest("Agua", "20", false),
            key,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, incompatibleResponse.StatusCode);
        using var problem = await JsonDocument.ParseAsync(
            await incompatibleResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal(
            "catalog.product.idempotency_key_conflict",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Single(await ListProductsAsync(cancellationToken));
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_have_one_persistent_effect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var key = NewIdempotencyKey();
        var request = new CreateProductRequest("Cerveza Roja", "20", false);

        var responses = await Task.WhenAll(
            PostProductAsync(request, key, cancellationToken),
            PostProductAsync(request, key, cancellationToken));

        try
        {
            Assert.All(
                responses,
                response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            var products = await Task.WhenAll(
                responses.Select(response =>
                    response.Content.ReadFromJsonAsync<ProductResponse>(cancellationToken)));
            Assert.NotNull(products[0]);
            Assert.Equal(products[0], products[1]);
            Assert.Equal(products[0], Assert.Single(await ListProductsAsync(cancellationToken)));
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
    public async Task Replay_is_resolved_from_postgresql_after_backend_restart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        var key = NewIdempotencyKey();
        var request = new CreateProductRequest("Tónica", "15.50", false);

        using var firstResponse = await PostProductAsync(request, key, cancellationToken);
        var firstProduct = await firstResponse.Content.ReadFromJsonAsync<ProductResponse>(
            cancellationToken);

        await fixture.RestartApplicationAsync(cancellationToken);

        using var replayResponse = await PostProductAsync(request, key, cancellationToken);
        var replayedProduct = await replayResponse.Content.ReadFromJsonAsync<ProductResponse>(
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.Equal(firstProduct, replayedProduct);
        Assert.Equal(firstProduct, Assert.Single(await ListProductsAsync(cancellationToken)));
    }

    [Fact]
    public async Task Duplicate_operational_name_with_different_case_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);
        using var first = await PostProductAsync(
            new CreateProductRequest("Gaseosa Limón", "10.00", false),
            NewIdempotencyKey(),
            cancellationToken);
        using var duplicate = await PostProductAsync(
            new CreateProductRequest("gASEOSA lIMÓN", "12.00", false),
            NewIdempotencyKey(),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var problem = await JsonDocument.ParseAsync(
            await duplicate.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal(
            "catalog.product.operational_name_conflict",
            problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Concurrent_equivalent_names_with_different_keys_create_only_one_product()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);

        var responses = await Task.WhenAll(
            PostProductAsync(
                new CreateProductRequest("Cerveza Roja", "20", false),
                NewIdempotencyKey(),
                cancellationToken),
            PostProductAsync(
                new CreateProductRequest("cERVEZA rOJA", "20", false),
                NewIdempotencyKey(),
                cancellationToken));

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            Assert.Single(await ListProductsAsync(cancellationToken));
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
    public async Task Requires_preparation_must_be_explicitly_false()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);

        using var trueResponse = await PostProductAsync(
            new CreateProductRequest("Producto preparado", "10", true),
            NewIdempotencyKey(),
            cancellationToken);
        using var omittedResponse = await PostProductAsync(
            new CreateProductRequest("Producto ambiguo", "10", null),
            NewIdempotencyKey(),
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, trueResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, omittedResponse.StatusCode);
    }

    [Fact]
    public async Task Group_is_not_accepted_in_the_I1_creation_contract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);

        using var response = await PostProductAsync(
            new
            {
                operationalName = "Agua",
                price = "10",
                requiresPreparation = false,
                group = "Bebidas"
            },
            NewIdempotencyKey(),
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Accents_and_punctuation_are_not_additional_name_equivalences()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(cancellationToken);

        foreach (var name in new[] { "Cafe", "Café", "Cafe!" })
        {
            using var response = await PostProductAsync(
                new CreateProductRequest(name, "10", false),
                NewIdempotencyKey(),
                cancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        Assert.Equal(3, (await ListProductsAsync(cancellationToken)).Length);
    }

    [Fact]
    public async Task OpenApi_describes_required_fields_and_string_price()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await fixture.Client.GetAsync(
            "/openapi/v1.json",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        Assert.Equal(
            "string",
            schemas.GetProperty(nameof(CreateProductRequest))
                .GetProperty("properties")
                .GetProperty("price")
                .GetProperty("type")
                .GetString());
        Assert.Equal(
            "string",
            schemas.GetProperty(nameof(ProductResponse))
                .GetProperty("properties")
                .GetProperty("price")
                .GetProperty("type")
                .GetString());

        var requiredRequestProperties = schemas.GetProperty(nameof(CreateProductRequest))
            .GetProperty("required")
            .EnumerateArray()
            .Select(property => property.GetString())
            .ToArray();
        Assert.Contains("requiresPreparation", requiredRequestProperties);

        var headerParameter = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/catalog/products")
            .GetProperty("post")
            .GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.True(headerParameter.GetProperty("required").GetBoolean());
    }

    private async Task<HttpResponseMessage> PostProductAsync(
        object request,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/products")
        {
            Content = JsonContent.Create(request, request.GetType())
        };

        if (idempotencyKey is not null)
        {
            message.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await fixture.Client.SendAsync(message, cancellationToken);
    }

    private async Task<ProductResponse[]> ListProductsAsync(
        CancellationToken cancellationToken)
    {
        using var response = await fixture.Client.GetAsync(
            "/api/catalog/products",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        return Assert.IsType<ProductResponse[]>(
            await response.Content.ReadFromJsonAsync<ProductResponse[]>(cancellationToken));
    }

    private static string NewIdempotencyKey() => Guid.NewGuid().ToString("D");
}
