using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

internal static class DeliveryQuantityTestSupport
{
    internal static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        Guid incorporationId,
        int contentOrdinal,
        Guid? idempotencyKey,
        int quantity,
        CancellationToken cancellationToken) =>
        await PostAsync(
            client,
            incorporationId.ToString("D"),
            contentOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            idempotencyKey?.ToString("D"),
            JsonContent.Create(new DeliverQuantityRequest(quantity)),
            includeAntiforgery: true,
            cancellationToken);

    internal static async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        Guid incorporationId,
        int contentOrdinal,
        Guid idempotencyKey,
        string json,
        CancellationToken cancellationToken) =>
        await PostAsync(
            client,
            incorporationId.ToString("D"),
            contentOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            idempotencyKey.ToString("D"),
            new StringContent(json, Encoding.UTF8, "application/json"),
            includeAntiforgery: true,
            cancellationToken);

    internal static async Task<HttpResponseMessage> PostWithRawTargetAsync(
        HttpClient client,
        string incorporationId,
        string contentOrdinal,
        string? idempotencyKey,
        int quantity,
        bool includeAntiforgery,
        CancellationToken cancellationToken) =>
        await PostAsync(
            client,
            incorporationId,
            contentOrdinal,
            idempotencyKey,
            JsonContent.Create(new DeliverQuantityRequest(quantity)),
            includeAntiforgery,
            cancellationToken);

    internal static async Task<DeliverQuantityResponse> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<DeliverQuantityResponse>(
            await response.Content.ReadFromJsonAsync<DeliverQuantityResponse>(
                cancellationToken));
    }

    internal static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using var problem = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    internal static async Task<DeliveryTarget> CreateDirectAsync(
        OrderOperationsApiFixture fixture,
        int quantity,
        CancellationToken cancellationToken,
        string productName = "Directo")
    {
        var product = await fixture.CreateProductAsync(
            productName,
            "5",
            cancellationToken);
        var confirmation = await ConfirmAsync(
            fixture.OrderOperationsClient,
            product.Id,
            quantity,
            cancellationToken);
        var content = Assert.Single(await fixture.ReadConfirmedContentsAsync(cancellationToken));
        return new DeliveryTarget(
            confirmation.OperationalReference,
            content.IncorporationId,
            content.ContentOrdinal,
            null);
    }

    internal static async Task<DeliveryTarget> CreatePreparedAsync(
        OrderOperationsApiFixture fixture,
        int quantity,
        int ready,
        CancellationToken cancellationToken,
        string productName = "Preparado")
    {
        var responsibilityId = Guid.CreateVersion7();
        var created = await fixture.CreatePreparedWorkAsync(
            responsibilityId,
            cancellationToken,
            quantity,
            productName);
        var content = Assert.Single(await fixture.ReadConfirmedContentsAsync(cancellationToken));
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(cancellationToken));
        await fixture.SetPreparationQuantitiesAsync(
            work.Id,
            quantity - ready,
            0,
            ready,
            cancellationToken);
        return new DeliveryTarget(
            created.Confirmation.OperationalReference,
            content.IncorporationId,
            content.ContentOrdinal,
            work.Id);
    }

    private static async Task<FirstConfirmationResponse> ConfirmAsync(
        HttpClient client,
        Guid productId,
        int quantity,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                "Mesa Delivery",
                [new FirstConfirmationItemRequest(productId, quantity)]))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            client,
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(
                cancellationToken));
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string incorporationId,
        string contentOrdinal,
        string? idempotencyKey,
        HttpContent content,
        bool includeAntiforgery,
        CancellationToken cancellationToken)
    {
        string? requestToken = null;
        if (includeAntiforgery)
        {
            using var response = await client.GetAsync(
                "/api/security/antiforgery",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            requestToken = document.RootElement.GetProperty("requestToken").GetString();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/incorporations/" + incorporationId +
            "/contents/" + contentOrdinal + "/deliver")
        {
            Content = content
        };
        if (requestToken is not null)
        {
            request.Headers.Add("X-NexoBar-CSRF", requestToken);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request, cancellationToken);
    }
}

internal sealed record DeliveryTarget(
    string OperationalReference,
    Guid IncorporationId,
    int ContentOrdinal,
    Guid? WorkId);
