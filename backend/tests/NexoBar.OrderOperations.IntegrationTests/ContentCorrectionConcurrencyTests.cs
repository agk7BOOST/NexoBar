using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ContentCorrectionConcurrencyTests(OrderOperationsApiFixture fixture)
{
    [Theory]
    [InlineData("start", true)]
    [InlineData("start", false)]
    [InlineData("delivery", true)]
    [InlineData("delivery", false)]
    [InlineData("correction", true)]
    [InlineData("correction", false)]
    public async Task Competing_exact_quantities_revalidate_after_order_lock(string competing, bool correctionFirst)
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
            Task<HttpResponseMessage> Correct() => ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 2, token);
            Task<HttpResponseMessage> Compete() => competing switch
            {
                "start" => PreparationStartTestSupport.PostAsync(actor, target.WorkId!.Value, Guid.NewGuid(), 2, token),
                "delivery" => DeliveryQuantityTestSupport.PostAsync(actor, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 2, token),
                _ => Correct()
            };
            var first = correctionFirst ? Correct() : Compete();
            Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
            var second = correctionFirst ? Compete() : Correct();
            Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), token));
            await blocker.CommitAsync(token);
            using var firstResponse = await first;
            using var secondResponse = await second;
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            var removed = correctionFirst || competing == "correction" ? 2 : 0;
            Assert.Equal(removed, (await db.ContentQuantityStates.SingleAsync(token)).RemovedByCorrectionQuantity);
            Assert.Equal(!correctionFirst && competing == "delivery" ? 2 : 0, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
            if (competing == "start")
            {
                var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
                Assert.Equal(correctionFirst ? 0 : 2, work.InPreparationQuantity);
                Assert.Equal(1, work.PendingQuantity);
            }
            await ContentCorrectionTestSupport.CountsAsync(fixture, removed == 0 ? 0 : 1, token);
        }
        finally { if (competing == "start") actor.Dispose(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Liquidation_revalidates_corrected_obligation(bool correctionFirst)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 2, token);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, token);
        Task<HttpResponseMessage> Correct() => ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        Task<HttpResponseMessage> Liquidate() => LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), token);
        var first = correctionFirst ? Correct() : Liquidate();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
        var second = correctionFirst ? Liquidate() : Correct();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), token));
        await blocker.CommitAsync(token);
        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal(correctionFirst ? HttpStatusCode.OK : HttpStatusCode.Conflict, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var read = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, token);
        Assert.Equal("10", read.FunctionalAmount);
        Assert.Equal(correctionFirst, read.IsFrozen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Subsequent_confirmation_preserves_exact_corrected_content(bool correctionFirst)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        var product = Assert.Single(await fixture.ReadConfirmedContentsAsync(token)).ProductId;
        var pending = await fixture.StartPendingCompositionAsync(target.OperationalReference, token);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, token);
        Task<HttpResponseMessage> Correct() => ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 3, token);
        async Task<HttpResponseMessage> Confirm()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/order-operations/orders/{target.OperationalReference}/confirmations")
            { Content = JsonContent.Create(new SubsequentConfirmationRequest(pending.PendingCompositionId, [new(product, 2)])) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, token);
        }
        var first = correctionFirst ? Correct() : Confirm();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
        var second = correctionFirst ? Confirm() : Correct();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), token));
        await blocker.CommitAsync(token);
        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal(correctionFirst ? HttpStatusCode.OK : HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(correctionFirst ? HttpStatusCode.Created : HttpStatusCode.OK, secondResponse.StatusCode);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Equal(3, (await db.ContentQuantityStates.SingleAsync(x => x.IncorporationId == target.IncorporationId, token)).RemovedByCorrectionQuantity);
        Assert.Equal(0, (await db.ContentQuantityStates.SingleAsync(x => x.IncorporationId != target.IncorporationId, token)).RemovedByCorrectionQuantity);
        Assert.Equal(2, await db.ConfirmationHistory.CountAsync(token));
        Assert.Empty(await db.PendingCompositions.ToArrayAsync(token));
    }

    [Fact]
    public async Task Same_key_concurrent_corrections_have_one_effect()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 3, 0, token);
        var key = Guid.NewGuid();
        var responses = await Task.WhenAll(ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 2, token),
            ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 2, token));
        using var first = responses[0]; using var second = responses[1];
        Assert.Equal(await ContentCorrectionTestSupport.SuccessAsync(first, token), await ContentCorrectionTestSupport.SuccessAsync(second, token));
        Assert.Equal(1, Assert.Single(await fixture.ReadPreparationWorkAsync(token)).PendingQuantity);
        await ContentCorrectionTestSupport.CountsAsync(fixture, 1, token);
    }
}
