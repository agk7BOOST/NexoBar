using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

internal static class PreparationReadyTestSupport
{
    internal static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        Guid workId,
        Guid? idempotencyKey,
        int quantity,
        CancellationToken cancellationToken) =>
        PostAsync(
            client,
            workId.ToString("D"),
            idempotencyKey?.ToString("D"),
            JsonContent.Create(new MarkPreparationQuantityReadyRequest(quantity)),
            cancellationToken);

    internal static Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        Guid workId,
        Guid idempotencyKey,
        string json,
        CancellationToken cancellationToken) =>
        PostAsync(
            client,
            workId.ToString("D"),
            idempotencyKey.ToString("D"),
            new StringContent(json, Encoding.UTF8, "application/json"),
            cancellationToken);

    internal static Task<HttpResponseMessage> PostWithRawIdentifiersAsync(
        HttpClient client,
        string workId,
        string? idempotencyKey,
        int quantity,
        CancellationToken cancellationToken) =>
        PostAsync(
            client,
            workId,
            idempotencyKey,
            JsonContent.Create(new MarkPreparationQuantityReadyRequest(quantity)),
            cancellationToken);

    internal static async Task<MarkPreparationQuantityReadyResponse> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<MarkPreparationQuantityReadyResponse>(
            await response.Content.ReadFromJsonAsync<MarkPreparationQuantityReadyResponse>(
                cancellationToken));
    }

    internal static Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        CancellationToken cancellationToken) =>
        PreparationStartTestSupport.AssertProblemAsync(
            response,
            status,
            code,
            cancellationToken);

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string workId,
        string? idempotencyKey,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var requestToken = await GetAntiforgeryTokenAsync(client, cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/preparation/work/{workId}/ready")
        {
            Content = content
        };
        request.Headers.Add("X-NexoBar-CSRF", requestToken);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            "/api/security/antiforgery",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return Assert.IsType<string>(
            document.RootElement.GetProperty("requestToken").GetString());
    }
}
