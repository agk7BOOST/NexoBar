using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ContentCancellationCompositionTests(OrderOperationsApiFixture fixture)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cancellation_and_correction_preserve_separate_quantities_and_history(bool prepared, bool cancellationFirst)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = prepared ? await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 6, 0, token)
            : await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 6, token);
        var pending = await fixture.StartPendingCompositionAsync(target.OperationalReference, token);
        var key = Guid.NewGuid();
        async Task Cancel()
        {
            using var response = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 2, token);
            var result = await ContentCancellationTestSupport.SuccessAsync(response, token);
            Assert.Equal(cancellationFirst ? 6 : 5, result.PreviousFulfillmentQuantity);
            Assert.Equal(cancellationFirst ? 4 : 3, result.ResultingFulfillmentQuantity);
        }
        async Task Correct()
        {
            using var response = await ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 1, token);
            var result = await ContentCorrectionTestSupport.SuccessAsync(response, token);
            Assert.Equal(cancellationFirst ? 4 : 6, result.PreviousFulfillmentQuantity);
            Assert.Equal(cancellationFirst ? 3 : 5, result.ResultingFulfillmentQuantity);
        }
        if (cancellationFirst) { await Cancel(); await Correct(); }
        else { await Correct(); await Cancel(); }
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var state = await db.ContentQuantityStates.SingleAsync(token);
        Assert.Equal(1, state.RemovedByCorrectionQuantity);
        Assert.Equal(2, state.CancelledQuantity);
        Assert.Equal(6, (await db.IncorporationContents.SingleAsync(token)).Quantity);
        Assert.Equal(pending.PendingCompositionId, (await db.PendingCompositions.SingleAsync(token)).Id);
        Assert.Equal("ContentQuantityCancelled", (await db.ContentCancellationHistory.SingleAsync(token)).EventKind);
        Assert.Equal("ContentQuantityCorrected", (await db.ContentCorrectionHistory.SingleAsync(token)).EventKind);
        if (prepared)
        {
            var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
            using var actor = await fixture.LoginAsync(await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, token), token);
            using var start = await PreparationStartTestSupport.PostAsync(actor, work.Id, Guid.NewGuid(), 3, token);
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            using var ready = await PreparationReadyTestSupport.PostAsync(actor, work.Id, Guid.NewGuid(), 3, token);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }
        await ContentCancellationTestSupport.DeliverAsync(fixture, target, 3, token);
        using var read = await fixture.OrderOperationsClient.GetAsync($"/api/order-operations/orders/{target.OperationalReference}/delivery", token);
        read.EnsureSuccessStatusCode();
        var item = Assert.Single((await read.Content.ReadFromJsonAsync<OrderDeliveryResponse>(token))!.Contents);
        Assert.Equal(6, item.ConfirmedQuantity);
        Assert.Equal(1, item.RemovedByCorrectionQuantity);
        Assert.Equal(2, item.CancelledQuantity);
        Assert.Equal(3, item.CurrentFulfillmentQuantity);
        Assert.Equal(0, item.DeliverableQuantity);
        var order = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, token);
        Assert.Equal(["pending_composition"], order.LiquidationBlockers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_backfills_C_preserves_R_and_protects_cancellation(bool prepared)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = prepared ? await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 5, 0, token)
            : await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 5, token);
        using var correction = await ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        await ContentCorrectionTestSupport.SuccessAsync(correction, token);
        var original = await fixture.ReadConfirmedContentsAsync(token);
        var work = await fixture.ReadPreparationWorkAsync(token);
        try
        {
            await fixture.MigrateOrderOperationsAsync("20260906163222_AddContentCorrection", token);
            await fixture.MigrateOrderOperationsAsync("20260907054805_AddContentCancellation", token);
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            var state = await db.ContentQuantityStates.AsNoTracking().SingleAsync(token);
            Assert.Equal(0, state.CancelledQuantity);
            Assert.Equal(1, state.RemovedByCorrectionQuantity);
            Assert.Equal(original, await fixture.ReadConfirmedContentsAsync(token));
            Assert.Equal(work, await fixture.ReadPreparationWorkAsync(token));
            using var cancel = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
            await ContentCancellationTestSupport.SuccessAsync(cancel, token);
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                fixture.MigrateOrderOperationsAsync("20260906163222_AddContentCorrection", token));
            Assert.Contains("Cannot remove Content Cancellation", error.MessageText);
            Assert.Equal(1, (await db.ContentQuantityStates.AsNoTracking().SingleAsync(token)).CancelledQuantity);
            await ContentCancellationTestSupport.CountsAsync(fixture, 1, token);
        }
        finally { await fixture.MigrateOrderOperationsAsync("20260907054805_AddContentCancellation", token); }
    }
}
