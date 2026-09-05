using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NexoBar.OrderOperations.IntegrationTests;

internal static class LiquidationTestSupport
{
    internal static async Task<FirstConfirmationResponse> CreateDirectOrderAsync(
        OrderOperationsApiFixture fixture,
        IReadOnlyList<(string Price, int Quantity)> lines,
        CancellationToken cancellationToken)
    {
        var items = new List<FirstConfirmationItemRequest>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            var product = await fixture.CreateProductAsync(
                $"Liquidation product {index} {Guid.NewGuid():N}",
                lines[index].Price,
                cancellationToken);
            items.Add(new FirstConfirmationItemRequest(product.Id, lines[index].Quantity));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                "Mesa Liquidation",
                items))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient,
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(
                cancellationToken));
    }

    internal static Task<HttpResponseMessage> PostSimpleAsync(
        HttpClient client,
        string operationalReference,
        Guid idempotencyKey,
        string? declaredPaymentMedium,
        CancellationToken cancellationToken) =>
        PostAsync(
            client,
            $"/api/order-operations/orders/{operationalReference}/liquidate-simple",
            idempotencyKey,
            JsonContent.Create(new LiquidateSimpleRequest(declaredPaymentMedium)),
            cancellationToken);

    internal static Task<HttpResponseMessage> PostExternalAsync(
        HttpClient client,
        string operationalReference,
        Guid idempotencyKey,
        CancellationToken cancellationToken) =>
        PostAsync(
            client,
            $"/api/order-operations/orders/{operationalReference}/record-external-collection",
            idempotencyKey,
            content: null,
            cancellationToken: cancellationToken);

    internal static async Task<LiquidationResponse> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<LiquidationResponse>(
            await response.Content.ReadFromJsonAsync<LiquidationResponse>(
                cancellationToken));
    }

    internal static async Task<OrderQueryResponse> ReadOrderAsync(
        HttpClient client,
        string operationalReference,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"/api/order-operations/orders/{operationalReference}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<OrderQueryResponse>(
            await response.Content.ReadFromJsonAsync<OrderQueryResponse>(
                cancellationToken));
    }

    internal static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        Guid idempotencyKey,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = content;
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            client,
            request,
            cancellationToken);
    }
}
