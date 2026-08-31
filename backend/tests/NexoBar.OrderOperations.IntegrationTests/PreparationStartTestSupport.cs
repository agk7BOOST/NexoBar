using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

internal static class PreparationStartTestSupport
{
    internal static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        Guid workId,
        Guid? idempotencyKey,
        int quantity,
        CancellationToken cancellationToken) =>
        await PostAsync(
            client,
            workId.ToString("D"),
            idempotencyKey?.ToString("D"),
            JsonContent.Create(new StartPreparationQuantityRequest(quantity)),
            cancellationToken);

    internal static async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        Guid workId,
        Guid idempotencyKey,
        string json,
        CancellationToken cancellationToken) =>
        await PostAsync(
            client,
            workId.ToString("D"),
            idempotencyKey.ToString("D"),
            new StringContent(json, Encoding.UTF8, "application/json"),
            cancellationToken);

    internal static async Task<HttpResponseMessage> PostWithRawIdentifiersAsync(
        HttpClient client,
        string workId,
        string? idempotencyKey,
        int quantity,
        CancellationToken cancellationToken) =>
        await PostAsync(
            client,
            workId,
            idempotencyKey,
            JsonContent.Create(new StartPreparationQuantityRequest(quantity)),
            cancellationToken);

    internal static async Task<StartPreparationQuantityResponse> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<StartPreparationQuantityResponse>(
            await response.Content.ReadFromJsonAsync<StartPreparationQuantityResponse>(
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
            $"/api/order-operations/preparation/work/{workId}/start")
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
