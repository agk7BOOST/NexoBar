using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.OrderOperations.IntegrationTests;

internal static class ContentCorrectionTestSupport
{
    internal static string Path(DeliveryTarget target) =>
        $"/api/order-operations/orders/{target.OperationalReference}/incorporations/{target.IncorporationId}/contents/{target.ContentOrdinal}/correct-content-quantity";

    internal static async Task<HttpResponseMessage> PostAsync(HttpClient client, DeliveryTarget target, Guid key, int quantity, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(target)) { Content = JsonContent.Create(new CorrectContentRequest(quantity)) };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, token);
    }

    internal static async Task<ContentCorrectionResponse> SuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<ContentCorrectionResponse>(await response.Content.ReadFromJsonAsync<ContentCorrectionResponse>(token));
    }

    internal static Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, CancellationToken token) =>
        DeliveryQuantityTestSupport.AssertProblemAsync(response, status, "order_operations.content_correction." + code, token);

    internal static async Task DeliverAsync(OrderOperationsApiFixture fixture, DeliveryTarget target, int quantity, CancellationToken token)
    {
        using var response = await DeliveryQuantityTestSupport.PostAsync(fixture.OrderOperationsClient, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), quantity, token);
        await DeliveryQuantityTestSupport.ReadSuccessAsync(response, token);
    }

    internal static async Task CountsAsync(OrderOperationsApiFixture fixture, int expected, CancellationToken token)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Equal(expected, await db.ContentCorrectionHistory.CountAsync(token));
        Assert.Equal(expected, await db.ContentCorrectionCommands.CountAsync(token));
    }
}
