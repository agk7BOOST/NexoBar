using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class TerminalOrderHistoryProjectionTests(OrderOperationsApiFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Applied_price_correction_is_projected_with_original_corrected_and_effective_prices()
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 1, Token);
        var content = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token));
        await SetCatalogPriceAsync(content.ProductId, 8m);
        using (var correction = await PostAsync($"/api/order-operations/orders/{target.OperationalReference}/incorporations/{target.IncorporationId}/contents/{target.ContentOrdinal}/apply-current-catalog-price", new ApplyCurrentCatalogPriceRequest()))
            correction.EnsureSuccessStatusCode();
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 1, Token);
        await EndOrderAsync(target.OperationalReference);

        var item = Assert.Single((await ReadAsync(target.OperationalReference)).Incorporations).Contents[0];
        Assert.Equal("5", item.AppliedPrice);
        Assert.Equal("8", item.EffectiveAppliedPrice);
        var price = Assert.Single(item.PriceCorrections);
        Assert.Equal("5", price.PreviousPrice);
        Assert.Equal("8", price.ResultingPrice);
        Assert.Equal(fixture.DefaultOrderOperationsActor.IdentityId, price.ActorIdentityId);
        Assert.NotEqual(default, price.OccurredAtUtc);
    }

    [Fact]
    public async Task Content_correction_and_cancellation_remain_distinct_in_terminal_projection()
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 5, Token);
        using (var correction = await ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, Token)) correction.EnsureSuccessStatusCode();
        using (var cancellation = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, Token)) cancellation.EnsureSuccessStatusCode();
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 3, Token);
        await EndOrderAsync(target.OperationalReference);

        var item = Assert.Single((await ReadAsync(target.OperationalReference)).Incorporations).Contents[0];
        var correctionFact = Assert.Single(item.Corrections);
        var cancellationFact = Assert.Single(item.Cancellations);
        Assert.Equal("Correction", correctionFact.Type);
        Assert.Equal(0, correctionFact.PreviousQuantity);
        Assert.Equal(1, correctionFact.ResultingQuantity);
        Assert.Equal("Cancellation", cancellationFact.Type);
        Assert.Equal(0, cancellationFact.PreviousQuantity);
        Assert.Equal(1, cancellationFact.ResultingQuantity);
        Assert.Equal(1, item.RemovedByCorrectionQuantity);
        Assert.Equal(1, item.CancelledQuantity);
        Assert.Equal(fixture.DefaultOrderOperationsActor.IdentityId, correctionFact.ActorIdentityId);
        Assert.Equal(fixture.DefaultOrderOperationsActor.IdentityId, cancellationFact.ActorIdentityId);
        Assert.NotEqual(default, correctionFact.OccurredAtUtc);
        Assert.NotEqual(default, cancellationFact.OccurredAtUtc);
    }

    [Fact]
    public async Task Delivery_correction_keeps_original_delivery_and_projects_effective_total()
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, Token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 2, Token);
        using (var correction = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, Token)) correction.EnsureSuccessStatusCode();
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 1, Token);
        await EndOrderAsync(target.OperationalReference);

        var item = Assert.Single((await ReadAsync(target.OperationalReference)).Incorporations).Contents[0];
        Assert.Equal(2, item.Deliveries.Count);
        Assert.Contains(item.Deliveries, x => x.Quantity == 2 && x.ResultingDeliveredQuantity == 2);
        var correctionFact = Assert.Single(item.DeliveryCorrections);
        Assert.Equal(2, correctionFact.PreviousDeliveredQuantity);
        Assert.Equal(1, correctionFact.ResultingDeliveredQuantity);
        Assert.Equal(2, item.EffectiveDeliveredQuantity);
        Assert.Equal(fixture.DefaultOrderOperationsActor.IdentityId, correctionFact.ActorIdentityId);
        Assert.NotEqual(default, correctionFact.OccurredAtUtc);
    }

    [Fact]
    public async Task Preparation_correction_preserves_start_and_later_progress_in_terminal_projection()
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 2, 0, Token);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        var preparationActor = await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, Token);
        using var preparer = await fixture.LoginAsync(preparationActor, Token);
        using (var start = await PreparationStartTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), 2, Token)) start.EnsureSuccessStatusCode();
        using (var correction = await PostAsync(preparer, $"/api/order-operations/preparation/work/{work.Id}/correct-start", new CorrectPreparationProgressRequest(1))) correction.EnsureSuccessStatusCode();
        using (var restart = await PreparationStartTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), 1, Token)) restart.EnsureSuccessStatusCode();
        using (var ready = await PreparationReadyTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), 2, Token)) ready.EnsureSuccessStatusCode();
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 2, Token);
        await EndOrderAsync(target.OperationalReference);

        var history = Assert.Single((await ReadAsync(target.OperationalReference)).Incorporations).Contents[0].PreparationHistory;
        Assert.Contains(history, x => x.Type == PreparationHistory.QuantityStartedEventKind && x.Quantity == 2);
        var corrected = Assert.Single(history, x => x.Type == PreparationHistory.StartCorrectedEventKind);
        Assert.Equal(1, corrected.Quantity);
        Assert.Equal(preparationActor.IdentityId, corrected.ActorIdentityId);
        Assert.NotEqual(default, corrected.OccurredAtUtc);
        Assert.Contains(history, x => x.Type == PreparationHistory.QuantityReadyEventKind && x.Quantity == 2);
    }

    [Fact]
    public async Task Unavailable_product_exception_applied_fact_is_returned_by_terminal_endpoint()
    {
        await fixture.ResetAsync(Token);
        var product = await fixture.CreateProductAsync("Terminal unavailable", "5", Token);
        await fixture.SetProductStateAsync(product.Id, true, false, Token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var client = await fixture.LoginAsync(actor, Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
        { Content = JsonContent.Create(new FirstConfirmationRequest("Mesa excepción", [new FirstConfirmationItemRequest(product.Id, 1, null, true)])) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var confirmationResponse = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);
        confirmationResponse.EnsureSuccessStatusCode();
        var confirmation = Assert.IsType<FirstConfirmationResponse>(await confirmationResponse.Content.ReadFromJsonAsync<FirstConfirmationResponse>(Token));
        var targetContent = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token));
        var target = new DeliveryTarget(confirmation.OperationalReference, targetContent.IncorporationId, targetContent.ContentOrdinal, null);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 1, Token);
        await EndOrderAsync(confirmation.OperationalReference);
        var item = Assert.Single((await ReadAsync(confirmation.OperationalReference)).Incorporations).Contents[0];
        Assert.Equal(product.Id, item.ProductId);
        Assert.True(item.UnavailableProductExceptionApplied);
    }

    private async Task SetCatalogPriceAsync(Guid productId, decimal price)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE catalog.products SET price = @price WHERE id = @id";
        command.Parameters.AddWithValue("price", price);
        command.Parameters.AddWithValue("id", productId);
        await command.ExecuteNonQueryAsync(Token);
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body) => PostAsync(fixture.OrderOperationsClient, path, body);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);
    }

    private async Task EndOrderAsync(string reference)
    {
        using (var liquidated = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, reference, Guid.NewGuid(), Token)) liquidated.EnsureSuccessStatusCode();
        using (var closed = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, reference, Guid.NewGuid(), Token)) closed.EnsureSuccessStatusCode();
    }

    private async Task<TerminalOrderHistory> ReadAsync(string reference)
    {
        using var response = await fixture.OrderOperationsClient.GetAsync($"/api/order-operations/order-history/{reference}", Token);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<TerminalOrderHistory>(await response.Content.ReadFromJsonAsync<TerminalOrderHistory>(Token));
    }
}
