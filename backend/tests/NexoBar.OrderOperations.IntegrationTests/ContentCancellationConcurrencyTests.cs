using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ContentCancellationConcurrencyTests(OrderOperationsApiFixture fixture)
{
    [Theory]
    [InlineData("start", true)]
    [InlineData("start", false)]
    [InlineData("delivery", true)]
    [InlineData("delivery", false)]
    [InlineData("cancellation", true)]
    [InlineData("cancellation", false)]
    [InlineData("correction", true)]
    [InlineData("correction", false)]
    public async Task Competing_exact_quantities_revalidate_after_order_lock(string competing, bool cancellationFirst)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = competing == "start" ? await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 3, 0, token)
            : await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        HttpClient actor = fixture.OrderOperationsClient;
        if (competing == "start")
        {
            var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
            actor = await fixture.LoginAsync(await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, token), token);
        }
        try
        {
            await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
            await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, token);
            Task<HttpResponseMessage> Cancel() => ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 2, token);
            Task<HttpResponseMessage> Compete() => competing switch
            {
                "start" => PreparationStartTestSupport.PostAsync(actor, target.WorkId!.Value, Guid.NewGuid(), 2, token),
                "correction" => ContentCorrectionTestSupport.PostAsync(actor, target, Guid.NewGuid(), 2, token),
                "delivery" => DeliveryQuantityTestSupport.PostAsync(actor, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 2, token),
                _ => Cancel()
            };
            var first = cancellationFirst ? Cancel() : Compete();
            Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
            var second = cancellationFirst ? Compete() : Cancel();
            Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), token));
            await blocker.CommitAsync(token);
            using var firstResponse = await first;
            using var secondResponse = await second;
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            var removed = cancellationFirst || competing == "cancellation" ? 2 : 0;
            Assert.Equal(removed, (await db.ContentQuantityStates.SingleAsync(token)).CancelledQuantity);
            Assert.Equal(!cancellationFirst && competing == "correction" ? 2 : 0,
                (await db.ContentQuantityStates.SingleAsync(token)).RemovedByCorrectionQuantity);
            Assert.Equal(!cancellationFirst && competing == "delivery" ? 2 : 0, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
            if (competing == "start")
            {
                var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
                Assert.Equal(cancellationFirst ? 0 : 2, work.InPreparationQuantity);
                Assert.Equal(1, work.PendingQuantity);
            }
            await ContentCancellationTestSupport.CountsAsync(fixture, removed == 0 ? 0 : 1, token);
        }
        finally { if (competing == "start") actor.Dispose(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Liquidation_revalidates_cancelled_obligation(bool cancellationFirst)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 2, token);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, token);
        Task<HttpResponseMessage> Cancel() => ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        Task<HttpResponseMessage> Liquidate() => LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), token);
        var first = cancellationFirst ? Cancel() : Liquidate();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
        var second = cancellationFirst ? Liquidate() : Cancel();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), token));
        await blocker.CommitAsync(token);
        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal(cancellationFirst ? HttpStatusCode.OK : HttpStatusCode.Conflict, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var read = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, token);
        Assert.Equal("10", read.FunctionalAmount);
        Assert.Equal(cancellationFirst, read.IsFrozen);
    }

    [Fact]
    public async Task Same_key_concurrent_cancellations_have_one_effect()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 3, 0, token);
        var key = Guid.NewGuid();
        var responses = await Task.WhenAll(ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 2, token),
            ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 2, token));
        using var first = responses[0]; using var second = responses[1];
        Assert.Equal(await ContentCancellationTestSupport.SuccessAsync(first, token), await ContentCancellationTestSupport.SuccessAsync(second, token));
        Assert.Equal(1, Assert.Single(await fixture.ReadPreparationWorkAsync(token)).PendingQuantity);
        await ContentCancellationTestSupport.CountsAsync(fixture, 1, token);
    }
}
