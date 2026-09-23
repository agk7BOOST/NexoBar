using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ProductOperationalNameSnapshotMigrationTests(
    OrderOperationsApiFixture fixture)
{
    private const string BeforeSnapshotMigration = "20260922150000_AddOrderContextChanges";
    private const string SnapshotMigration = "20260922160000_AddProductOperationalNameSnapshot";

    [Fact]
    public async Task Upgrade_backfills_creation_name_only_when_no_committed_rename_exists_and_repeats_on_reup()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        var unchangedProduct = await fixture.CreateProductAsync("Cerveza", "10", token);
        var unchanged = await ConfirmAsync(unchangedProduct.Id, token);

        var renamedAfterProduct = await fixture.CreateProductAsync("Pizza", "20", token);
        var renamedAfter = await ConfirmAsync(renamedAfterProduct.Id, token);
        await fixture.RenameProductDurablyAsync(
            renamedAfterProduct.Id, "Pizza", "Pizza grande", token);

        var renamedBeforeProduct = await fixture.CreateProductAsync("Agua", "5", token);
        await fixture.RenameProductDurablyAsync(
            renamedBeforeProduct.Id, "Agua", "Agua mineral", token);
        var renamedBefore = await ConfirmAsync(renamedBeforeProduct.Id, token);

        await fixture.MigrateOrderOperationsAsync(BeforeSnapshotMigration, token);
        await fixture.MigrateOrderOperationsAsync(SnapshotMigration, token);
        await AssertSnapshotAsync(unchanged.FirstIncorporation.Id, "Cerveza", token);
        await AssertSnapshotAsync(renamedAfter.FirstIncorporation.Id, null, token);
        await AssertSnapshotAsync(renamedBefore.FirstIncorporation.Id, null, token);
        Assert.Equal("Pizza grande", (await fixture.ReadProductAsync(
            renamedAfterProduct.Id, token)).OperationalName);

        await fixture.MigrateOrderOperationsAsync(BeforeSnapshotMigration, token);
        await fixture.MigrateOrderOperationsAsync(SnapshotMigration, token);
        await AssertSnapshotAsync(unchanged.FirstIncorporation.Id, "Cerveza", token);
        await AssertSnapshotAsync(renamedAfter.FirstIncorporation.Id, null, token);
        await AssertSnapshotAsync(renamedBefore.FirstIncorporation.Id, null, token);
        Assert.Equal("Pizza grande", (await fixture.ReadProductAsync(
            renamedAfterProduct.Id, token)).OperationalName);
        await fixture.ResetAsync(token);
    }

    private async Task<FirstConfirmationResponse> ConfirmAsync(
        Guid productId,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                "Mesa 7", [new FirstConfirmationItemRequest(productId, 1)]))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient, request, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
    }

    private async Task AssertSnapshotAsync(
        Guid incorporationId,
        string? expected,
        CancellationToken token)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var content = await context.IncorporationContents.AsNoTracking()
            .SingleAsync(row => row.IncorporationId == incorporationId, token);
        Assert.Equal(expected, content.ProductOperationalNameSnapshot);
    }
}
