using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ContentCorrectionPreservationTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Confirmation_replays_stay_original_and_pending_composition_survives_correction()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Original", "5", token);
        var firstKey = Guid.NewGuid();
        async Task<HttpResponseMessage> First()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
            { Content = JsonContent.Create(new FirstConfirmationRequest("Mesa", [new(product.Id, 3)])) };
            request.Headers.Add("Idempotency-Key", firstKey.ToString());
            return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, token);
        }
        using var first = await First();
        first.EnsureSuccessStatusCode();
        var originalFirst = await first.Content.ReadAsStringAsync(token);
        var order = (await first.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token))!;
        var pending = await fixture.StartPendingCompositionAsync(order.OperationalReference, token);
        var subsequentKey = Guid.NewGuid();
        async Task<HttpResponseMessage> Subsequent()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/order-operations/orders/{order.OperationalReference}/confirmations")
            { Content = JsonContent.Create(new SubsequentConfirmationRequest(pending.PendingCompositionId, [new(product.Id, 4)])) };
            request.Headers.Add("Idempotency-Key", subsequentKey.ToString());
            return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, token);
        }
        using var subsequent = await Subsequent();
        subsequent.EnsureSuccessStatusCode();
        var originalSubsequent = await subsequent.Content.ReadAsStringAsync(token);
        var newPending = await fixture.StartPendingCompositionAsync(order.OperationalReference, token);
        var contents = await fixture.ReadConfirmedContentsAsync(token);
        foreach (var content in contents)
        {
            var target = new DeliveryTarget(order.OperationalReference, content.IncorporationId, content.ContentOrdinal, null);
            using var corrected = await ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), content.IncorporationId == order.FirstIncorporation.Id ? 3 : 4, token);
            await ContentCorrectionTestSupport.SuccessAsync(corrected, token);
        }
        using var firstReplay = await First();
        using var subsequentReplay = await Subsequent();
        Assert.Equal(originalFirst, await firstReplay.Content.ReadAsStringAsync(token));
        Assert.Equal(originalSubsequent, await subsequentReplay.Content.ReadAsStringAsync(token));
        var read = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, order.OperationalReference, token);
        Assert.Equal(["pending_composition"], read.LiquidationBlockers);
        Assert.Equal(contents, await fixture.ReadConfirmedContentsAsync(token));
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var marker = await db.PendingCompositions.SingleAsync(token);
        Assert.Equal(newPending.PendingCompositionId, marker.Id);
        Assert.Equal(newPending.CreatedAt, marker.CreatedAt);
        Assert.Equal(newPending.CreatedByIdentityId, marker.CreatedByIdentityId);
        Assert.Equal(2, await db.ConfirmationHistory.CountAsync(token));
    }

    [Fact]
    public async Task Zero_obligation_is_readable_but_cannot_start_ready_deliver_or_correct_again()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 3, 0, token);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        using var preparer = await fixture.LoginAsync(await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, token), token);
        using var correction = await ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 3, token);
        await ContentCorrectionTestSupport.SuccessAsync(correction, token);
        using var read = await preparer.GetAsync($"/api/order-operations/preparation/work?preparationResponsibilityId={work.PreparationResponsibilityId}", token);
        read.EnsureSuccessStatusCode();
        var result = Assert.Single((await read.Content.ReadFromJsonAsync<PreparationWorkResponse[]>(token))!);
        Assert.Equal(0, result.TotalQuantity);
        using var start = await PreparationStartTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), 1, token);
        using var ready = await PreparationReadyTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), 1, token);
        using var deliver = await DeliveryQuantityTestSupport.PostAsync(fixture.OrderOperationsClient, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 1, token);
        using var again = await ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        Assert.All(new[] { start, ready, deliver, again }, response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
        using var liquidation = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), token);
        await LiquidationTestSupport.ReadSuccessAsync(liquidation, token);
    }

    [Fact]
    public async Task Migration_round_trip_preserves_existing_state_and_rejects_loss_of_correction_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 3, 0, token);
        var work = await fixture.ReadPreparationWorkAsync(token);
        var contents = await fixture.ReadConfirmedContentsAsync(token);
        try
        {
            await fixture.MigrateOrderOperationsAsync("20260906120000_AddContentQuantityState", token);
            await fixture.MigrateOrderOperationsAsync("20260906163222_AddContentCorrection", token);
            Assert.Equal(work, await fixture.ReadPreparationWorkAsync(token));
            Assert.Equal(contents, await fixture.ReadConfirmedContentsAsync(token));
            Assert.False(await fixture.HasPendingModelChangesAsync());
            using var correction = await ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 3, token);
            await ContentCorrectionTestSupport.SuccessAsync(correction, token);
            await Assert.ThrowsAsync<PostgresException>(() => fixture.MigrateOrderOperationsAsync("20260906120000_AddContentQuantityState", token));
            await ContentCorrectionTestSupport.CountsAsync(fixture, 1, token);
            Assert.Equal(0, Assert.Single(await fixture.ReadPreparationWorkAsync(token)).TotalQuantity);
        }
        finally { await fixture.MigrateOrderOperationsAsync("20260906163222_AddContentCorrection", token); }
    }
}
