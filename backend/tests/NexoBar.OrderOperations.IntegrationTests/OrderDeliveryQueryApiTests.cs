using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class OrderDeliveryQueryApiTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Delivery_preserves_Content_identity_order_and_authoritative_formulas()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var destination = Guid.CreateVersion7();
        var prepared = await fixture.CreateProductAsync("Pizza", "9", token);
        var direct = await fixture.CreateProductAsync("Agua", "3", token);
        await fixture.SetProductPreparationAsync(prepared.Id, destination, token);

        var first = await ConfirmFirstAsync(
            "Mesa 12",
            token,
            (prepared.Id, 5, "Sin cebolla"),
            (prepared.Id, 5, "Con extra queso"),
            (direct.Id, 5, null));
        var second = await ConfirmSubsequentAsync(
            first.OperationalReference,
            token,
            (prepared.Id, 5, "Bien cocida"),
            (direct.Id, 4, null));
        var work = await fixture.ReadPreparationWorkAsync(token);
        var pending = Assert.Single(work, item => item.Instruction == "Sin cebolla");
        var partialReady = Assert.Single(work, item => item.Instruction == "Con extra queso");
        var partialDelivered = Assert.Single(work, item => item.Instruction == "Bien cocida");
        await SetWorkQuantitiesAsync(partialReady, pending: 3, preparing: 0, ready: 2, token);
        await SetWorkQuantitiesAsync(
            partialDelivered,
            pending: 2,
            preparing: 0,
            ready: 3,
            token);
        var states = await fixture.ReadDeliveryStatesAsync(token);
        var directPartial = Assert.Single(states, state =>
            state.IncorporationId == first.FirstIncorporation.Id &&
            state.ProductId == direct.Id);
        var directComplete = Assert.Single(states, state =>
            state.IncorporationId == second.Incorporation.Id &&
            state.ProductId == direct.Id);
        await SetDeliveredAsync(directPartial.IncorporationId, directPartial.ContentOrdinal, 2, token);
        await SetDeliveredAsync(
            partialDelivered.IncorporationId,
            partialDelivered.ContentOrdinal,
            2,
            token);
        await SetDeliveredAsync(
            directComplete.IncorporationId,
            directComplete.ContentOrdinal,
            4,
            token);
        var actor = await fixture.CreateDeliveryActorAsync(
            hasOrderOperations: true,
            hasPreparation: false,
            enabledResponsibilityId: null,
            token);
        using var client = await fixture.LoginAsync(actor, token);

        var response = await ReadAsync(client, first.OperationalReference, token);

        Assert.Equal(Guid.Parse(first.OperationalReference), response.OrderId);
        Assert.Equal(first.OperationalReference, response.OperationalReference);
        Assert.Equal("Mesa 12", response.CurrentContext);
        Assert.Equal(5, response.Contents.Count);
        Assert.Equal(
            response.Contents.OrderBy(content => content.IncorporationOrdinal)
                .ThenBy(content => content.ContentOrdinal),
            response.Contents);
        Assert.Equal(2, response.Contents.Select(content => content.IncorporationId).Distinct().Count());

        AssertPrepared(
            Assert.Single(response.Contents, content => content.Instruction == "Sin cebolla"),
            ready: 0,
            delivered: 0,
            deliverable: 0,
            remaining: 5);
        AssertPrepared(
            Assert.Single(response.Contents, content => content.Instruction == "Con extra queso"),
            ready: 2,
            delivered: 0,
            deliverable: 2,
            remaining: 5);
        AssertPrepared(
            Assert.Single(response.Contents, content => content.Instruction == "Bien cocida"),
            ready: 3,
            delivered: 2,
            deliverable: 1,
            remaining: 3);
        AssertDirect(
            Assert.Single(response.Contents, content =>
                content.IncorporationId == first.FirstIncorporation.Id &&
                content.ProductId == direct.Id),
            total: 5,
            delivered: 2,
            deliverable: 3,
            remaining: 3);
        AssertDirect(
            Assert.Single(response.Contents, content =>
                content.IncorporationId == second.Incorporation.Id &&
                content.ProductId == direct.Id),
            total: 4,
            delivered: 4,
            deliverable: 0,
            remaining: 0);
        Assert.Equal(3, response.Contents.Count(content => content.ProductId == prepared.Id));
        Assert.Equal(3, response.Contents.Where(content => content.ProductId == prepared.Id)
            .Select(content => content.Instruction).Distinct().Count());
    }

    [Fact]
    public async Task Delivery_requires_only_current_OrderOperations_responsibility()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var direct = await fixture.CreateProductAsync("Agua", "3", token);
        var confirmation = await ConfirmFirstAsync(
            "Barra",
            token,
            (direct.Id, 1, null));
        var url = DeliveryUrl(confirmation.OperationalReference);

        using (var anonymous = await fixture.Client.GetAsync(url, token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var noResponsibility = await fixture.CreateDeliveryActorAsync(false, false, null, token);
        await AssertAccessAsync(noResponsibility, HttpStatusCode.Forbidden, token);

        var destination = Guid.CreateVersion7();
        var preparationOnly = await fixture.CreateDeliveryActorAsync(false, true, null, token);
        await AssertAccessAsync(preparationOnly, HttpStatusCode.Forbidden, token);

        var enablementOnly = await fixture.CreateDeliveryActorAsync(
            false,
            false,
            destination,
            token);
        await AssertAccessAsync(enablementOnly, HttpStatusCode.Forbidden, token);

        var authorized = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using (var client = await fixture.LoginAsync(authorized, token))
        using (var response = await client.GetAsync(url, token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var inactive = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using (var client = await fixture.LoginAsync(inactive, token))
        {
            await fixture.SetIdentityActiveAsync(inactive.IdentityId, false, token);
            using var response = await client.GetAsync(url, token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var revoked = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using (var client = await fixture.LoginAsync(revoked, token))
        {
            await fixture.RevokeSessionsAsync(revoked.IdentityId, token);
            using var response = await client.GetAsync(url, token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        async Task AssertAccessAsync(
            PreparationActor actor,
            HttpStatusCode expected,
            CancellationToken cancellationToken)
        {
            using var client = await fixture.LoginAsync(actor, cancellationToken);
            using var response = await client.GetAsync(url, cancellationToken);
            await AssertProblemAsync(
                response,
                expected,
                "order_operations.delivery.forbidden",
                cancellationToken);
        }
    }

    [Fact]
    public async Task Authorized_unknown_Order_is_not_found_without_hiding_forbidden_access()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await client.GetAsync(
            DeliveryUrl(Guid.CreateVersion7().ToString("D")),
            token);

        await AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            "order_operations.order.not_found",
            token);
    }

    [Theory]
    [InlineData(DeliveryCorruption.PreparedWithoutWork)]
    [InlineData(DeliveryCorruption.DirectWithWork)]
    [InlineData(DeliveryCorruption.MissingDeliveryState)]
    [InlineData(DeliveryCorruption.DeliveredAboveTotal)]
    [InlineData(DeliveryCorruption.PreparedDeliveredAboveReady)]
    public async Task Structural_or_quantity_corruption_returns_controlled_technical_error(
        DeliveryCorruption corruption)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var destination = Guid.CreateVersion7();
        var product = await fixture.CreateProductAsync("Producto", "5", token);
        if (corruption is DeliveryCorruption.PreparedWithoutWork or
            DeliveryCorruption.DirectWithWork or
            DeliveryCorruption.PreparedDeliveredAboveReady)
        {
            await fixture.SetProductPreparationAsync(product.Id, destination, token);
        }

        var confirmation = await ConfirmFirstAsync(
            "Mesa corrupta",
            token,
            (product.Id, 2, null));
        var incorporationId = confirmation.FirstIncorporation.Id;
        switch (corruption)
        {
            case DeliveryCorruption.PreparedWithoutWork:
                await ExecuteAsync(
                    "DELETE FROM order_operations.preparation_work " +
                    "WHERE incorporation_id = @incorporationId AND content_ordinal = 1",
                    token,
                    ("incorporationId", incorporationId));
                break;
            case DeliveryCorruption.DirectWithWork:
                await ExecuteAsync(
                    "UPDATE order_operations.incorporation_contents " +
                    "SET requires_preparation_at_confirmation = false " +
                    "WHERE incorporation_id = @incorporationId AND content_ordinal = 1",
                    token,
                    ("incorporationId", incorporationId));
                break;
            case DeliveryCorruption.MissingDeliveryState:
                await ExecuteAsync(
                    "DELETE FROM order_operations.delivery_states " +
                    "WHERE incorporation_id = @incorporationId AND content_ordinal = 1",
                    token,
                    ("incorporationId", incorporationId));
                break;
            case DeliveryCorruption.DeliveredAboveTotal:
                await SetDeliveredAsync(incorporationId, 1, 3, token);
                break;
            case DeliveryCorruption.PreparedDeliveredAboveReady:
                await SetWorkQuantitiesAsync(
                    Assert.Single(await fixture.ReadPreparationWorkAsync(token)),
                    pending: 1,
                    preparing: 0,
                    ready: 1,
                    token);
                await SetDeliveredAsync(incorporationId, 1, 2, token);
                break;
            default:
                throw new InvalidOperationException();
        }

        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);
        using var response = await client.GetAsync(
            DeliveryUrl(confirmation.OperationalReference),
            token);

        await AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "order_operations.delivery.state_inconsistent",
            token);
    }

    [Fact]
    public async Task Delivery_uses_one_distinct_batch_and_current_name_for_inactive_Product()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var destination = Guid.CreateVersion7();
        var product = await fixture.CreateProductAsync("Pizza", "9", token);
        await fixture.SetProductPreparationAsync(product.Id, destination, token);
        var confirmation = await ConfirmFirstAsync(
            "Mesa 3",
            token,
            (product.Id, 1, "A"),
            (product.Id, 1, "B"));
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using (var currentNameClient = await fixture.LoginAsync(actor, token))
        {
            var current = await ReadAsync(
                currentNameClient,
                confirmation.OperationalReference,
                token);
            Assert.All(current.Contents,
                content => Assert.Equal("Pizza", content.ProductOperationalName));
        }

        await fixture.RenameProductAsync(product.Id, "Pizza vigente", token);
        await fixture.SetProductStateAsync(product.Id, false, false, token);
        var observation = new ProductLookupObservation();
        await using var application = fixture.CreateApplicationWithProductLookupDecorator(
            services => new ObservingProductOperationalReferenceLookup(
                new ProductOperationalReferenceLookup(
                    services.GetRequiredService<CatalogDbContext>()),
                observation));
        using var client = await fixture.LoginAsync(actor, token, application);

        var response = await ReadAsync(client, confirmation.OperationalReference, token);

        Assert.Equal(2, response.Contents.Count);
        Assert.All(response.Contents,
            content => Assert.Equal("Pizza vigente", content.ProductOperationalName));
        Assert.Equal(1, observation.CallCount);
        Assert.Equal([product.Id], observation.RequestedProductIds);
    }

    [Fact]
    public async Task Missing_Product_has_no_UUID_fallback_and_returns_technical_error()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "3", token);
        var confirmation = await ConfirmFirstAsync(
            "Mesa 4",
            token,
            (product.Id, 1, null));
        var missingId = Guid.CreateVersion7();
        await fixture.ReplaceContentProductReferenceAsync(
            confirmation.FirstIncorporation.Id,
            1,
            missingId,
            token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await client.GetAsync(
            DeliveryUrl(confirmation.OperationalReference),
            token);
        var body = await response.Content.ReadAsStringAsync(token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("order_operations.delivery.product_reference_inconsistent", body);
        Assert.DoesNotContain(missingId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenApi_keeps_Delivery_contract_narrow_and_explicit()
    {
        var token = TestContext.Current.CancellationToken;
        using var response = await fixture.Client.GetAsync("/openapi/v1.json", token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(token),
            cancellationToken: token);
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/order-operations/orders/{operationalReference}/delivery")
            .GetProperty("get");
        Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("404", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("500", out _));

        var schema = document.RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(nameof(OrderDeliveryContentResponse))
            .GetProperty("properties");
        var expected = new[]
        {
            "incorporationId", "incorporationOrdinal", "contentOrdinal", "productId",
            "productOperationalName", "instruction", "totalQuantity",
            "requiresPreparationAtConfirmation", "readyQuantity", "deliveredQuantity",
            "deliverableQuantity", "remainingQuantity"
        };
        Assert.Equal(expected.Order(), schema.EnumerateObject().Select(property => property.Name).Order());
    }

    private async Task<FirstConfirmationResponse> ConfirmFirstAsync(
        string context,
        CancellationToken token,
        params (Guid ProductId, int Quantity, string? Instruction)[] items)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                context,
                items.Select(item => new FirstConfirmationItemRequest(
                    item.ProductId,
                    item.Quantity,
                    item.Instruction)).ToArray()))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await fixture.Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
    }

    private async Task<SubsequentConfirmationResponse> ConfirmSubsequentAsync(
        string operationalReference,
        CancellationToken token,
        params (Guid ProductId, int Quantity, string? Instruction)[] items)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{operationalReference}/confirmations")
        {
            Content = JsonContent.Create(new SubsequentConfirmationRequest(
                items.Select(item => new SubsequentConfirmationItemRequest(
                    item.ProductId,
                    item.Quantity,
                    item.Instruction)).ToArray()))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await fixture.Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<SubsequentConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<SubsequentConfirmationResponse>(token));
    }

    private static async Task<OrderDeliveryResponse> ReadAsync(
        HttpClient client,
        string operationalReference,
        CancellationToken token)
    {
        using var response = await client.GetAsync(DeliveryUrl(operationalReference), token);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<OrderDeliveryResponse>(
            await response.Content.ReadFromJsonAsync<OrderDeliveryResponse>(token));
    }

    private async Task SetWorkQuantitiesAsync(
        PreparationWorkSnapshot work,
        int pending,
        int preparing,
        int ready,
        CancellationToken token) =>
        await ExecuteAsync(
            "UPDATE order_operations.preparation_work " +
            "SET pending_quantity = @pending, in_preparation_quantity = @preparing, " +
            "ready_quantity = @ready WHERE id = @id",
            token,
            ("pending", pending),
            ("preparing", preparing),
            ("ready", ready),
            ("id", work.Id));

    private async Task SetDeliveredAsync(
        Guid incorporationId,
        int contentOrdinal,
        int delivered,
        CancellationToken token) =>
        await ExecuteAsync(
            "UPDATE order_operations.delivery_states SET delivered_quantity = @delivered " +
            "WHERE incorporation_id = @incorporationId AND content_ordinal = @contentOrdinal",
            token,
            ("delivered", delivered),
            ("incorporationId", incorporationId),
            ("contentOrdinal", contentOrdinal));

    private async Task ExecuteAsync(
        string sql,
        CancellationToken token,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
    }

    private static void AssertPrepared(
        OrderDeliveryContentResponse content,
        int ready,
        int delivered,
        int deliverable,
        int remaining)
    {
        Assert.True(content.RequiresPreparationAtConfirmation);
        Assert.Equal(5, content.TotalQuantity);
        Assert.Equal(ready, content.ReadyQuantity);
        Assert.Equal(delivered, content.DeliveredQuantity);
        Assert.Equal(deliverable, content.DeliverableQuantity);
        Assert.Equal(remaining, content.RemainingQuantity);
    }

    private static void AssertDirect(
        OrderDeliveryContentResponse content,
        int total,
        int delivered,
        int deliverable,
        int remaining)
    {
        Assert.False(content.RequiresPreparationAtConfirmation);
        Assert.Equal(total, content.TotalQuantity);
        Assert.Null(content.ReadyQuantity);
        Assert.Equal(delivered, content.DeliveredQuantity);
        Assert.Equal(deliverable, content.DeliverableQuantity);
        Assert.Equal(remaining, content.RemainingQuantity);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        CancellationToken token)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(token),
            cancellationToken: token);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static string DeliveryUrl(string operationalReference) =>
        $"/api/order-operations/orders/{operationalReference}/delivery";

    public enum DeliveryCorruption
    {
        PreparedWithoutWork,
        DirectWithWork,
        MissingDeliveryState,
        DeliveredAboveTotal,
        PreparedDeliveredAboveReady
    }
}
