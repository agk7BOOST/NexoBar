using System.Net;
using System.Text.Json;

namespace NexoBar.Inventory.IntegrationTests;

internal static class InventoryTestAssertions
{
    internal static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code,
        CancellationToken cancellationToken)
    {
        Assert.Equal(statusCode, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken));
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
