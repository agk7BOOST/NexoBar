using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class OrderQueryApiTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Existing_order_is_loaded_from_order_operations_persistence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10.50", cancellationToken);

        using var confirmation = await ConfirmAsync(product.Id, cancellationToken);
        confirmation.EnsureSuccessStatusCode();
        var confirmed = Assert.IsType<FirstConfirmationResponse>(
            await confirmation.Content.ReadFromJsonAsync<FirstConfirmationResponse>(
                cancellationToken));
        var orderId = Guid.Parse(confirmed.OperationalReference);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var orderOperations = scope.ServiceProvider
                .GetRequiredService<OrderOperationsDbContext>();
            await orderOperations.Orders
                .Where(order => order.Id == orderId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(order => order.Context, "Barra"),
                    cancellationToken);
            await orderOperations.ConfirmationHistory
                .Where(history => history.IncorporationId == confirmed.FirstIncorporation.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        history => history.ConfirmedContext,
                        "Different historical context"),
                    cancellationToken);

            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await catalog.Products
                .Where(candidate => candidate.Id == product.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(candidate => candidate.Price, 99.99m),
                    cancellationToken);
        }

        using var response = await fixture.Client.GetAsync(
            $"/api/order-operations/orders/{confirmed.OperationalReference}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = Assert.IsType<OrderQueryResponse>(
            await response.Content.ReadFromJsonAsync<OrderQueryResponse>(cancellationToken));
        Assert.Equal(confirmed.OperationalReference, order.OperationalReference);
        Assert.Equal("Barra", order.Context);
        var incorporation = Assert.Single(order.Incorporations);
        Assert.Equal(confirmed.FirstIncorporation.Id, incorporation.Id);
        Assert.Equal(1, incorporation.Ordinal);
        Assert.Equal(confirmed.FirstIncorporation.ConfirmedAt, incorporation.ConfirmedAt);
        var item = Assert.Single(incorporation.Items);
        Assert.Equal(product.Id, item.ProductId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal("10.50", item.AppliedPrice);
        Assert.Null(item.Instruction);
    }

    [Fact]
    public async Task Structurally_valid_unknown_reference_returns_not_found()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);

        using var response = await fixture.Client.GetAsync(
            $"/api/order-operations/orders/{Guid.CreateVersion7():D}",
            cancellationToken);

        await AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            "order_operations.order.not_found",
            cancellationToken);
    }

    [Fact]
    public async Task Structurally_invalid_reference_returns_bad_request_without_format_leak()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);

        using var response = await fixture.Client.GetAsync(
            "/api/order-operations/orders/not-a-valid-reference",
            cancellationToken);

        using var problem = await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.order.operational_reference_invalid",
            cancellationToken);
        var detail = problem.RootElement.GetProperty("detail").GetString();
        Assert.DoesNotContain("uuid", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Query_does_not_use_the_catalog_confirmation_capability()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10.50", cancellationToken);
        using var confirmation = await ConfirmAsync(product.Id, cancellationToken);
        confirmation.EnsureSuccessStatusCode();
        var confirmed = Assert.IsType<FirstConfirmationResponse>(
            await confirmation.Content.ReadFromJsonAsync<FirstConfirmationResponse>(
                cancellationToken));
        var replacement = new UnexpectedCatalogCapability();
        await using var application = fixture.CreateApplicationWithCatalog(replacement);
        using var client = application.CreateClient();

        using var response = await client.GetAsync(
            $"/api/order-operations/orders/{confirmed.OperationalReference}",
            cancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.False(replacement.WasCalled);
    }

    [Fact]
    public async Task OpenApi_describes_the_opaque_order_query_contract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await fixture.Client.GetAsync("/openapi/v1.json", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/order-operations/orders/{operationalReference}")
            .GetProperty("get");
        var parameter = Assert.Single(operation.GetProperty("parameters").EnumerateArray());
        Assert.Equal("operationalReference", parameter.GetProperty("name").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
        var parameterSchema = parameter.GetProperty("schema");
        AssertSchemaType(parameterSchema, "string");
        Assert.False(parameterSchema.TryGetProperty("format", out _));

        var responses = operation.GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out _));
        Assert.True(responses.TryGetProperty("400", out _));
        Assert.True(responses.TryGetProperty("404", out _));

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var operationalReferenceSchema = schemas.GetProperty(nameof(OrderQueryResponse))
            .GetProperty("properties").GetProperty("operationalReference");
        AssertSchemaType(operationalReferenceSchema, "string");
        Assert.False(operationalReferenceSchema.TryGetProperty("format", out _));
        AssertSchemaType(
            schemas.GetProperty(nameof(ConfirmedItemResponse))
                .GetProperty("properties").GetProperty("appliedPrice"),
            "string");
        AssertSchemaType(
            schemas.GetProperty(nameof(ConfirmedItemResponse))
                .GetProperty("properties").GetProperty("instruction"),
            "string");
    }

    private async Task<HttpResponseMessage> ConfirmAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                "Mesa 7",
                [new FirstConfirmationItemRequest(productId, 2)]))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await fixture.Client.SendAsync(request, cancellationToken);
    }

    private static async Task<JsonDocument> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        CancellationToken cancellationToken)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
        return problem;
    }

    private static void AssertSchemaType(JsonElement schema, string expectedType)
    {
        var type = schema.GetProperty("type");
        if (type.ValueKind == JsonValueKind.String)
        {
            Assert.Equal(expectedType, type.GetString());
            return;
        }

        Assert.Contains(
            expectedType,
            type.EnumerateArray().Select(value => value.GetString()));
    }
}
