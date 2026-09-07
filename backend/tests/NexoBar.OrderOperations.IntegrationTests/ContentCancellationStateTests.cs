using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ContentCancellationStateTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Mixed_work_preserves_started_ready_delivered_and_replay_is_original()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 6, 2, token);
        await fixture.SetPreparationQuantitiesAsync(target.WorkId!.Value, 3, 1, 2, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 1, token);
        var key = Guid.NewGuid();
        using var first = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 1, token);
        var original = await ContentCancellationTestSupport.SuccessAsync(first, token);
        using var second = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 2, token);
        var current = await ContentCancellationTestSupport.SuccessAsync(second, token);
        Assert.Equal(1, current.PreviousCancelledQuantity);
        Assert.Equal(3, current.ResultingCancelledQuantity);
        Assert.Equal(5, current.PreviousFulfillmentQuantity);
        Assert.Equal(3, current.ResultingFulfillmentQuantity);
        using var replay = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 1, token);
        Assert.Equal(original, await ContentCancellationTestSupport.SuccessAsync(replay, token));
        using var excess = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        await ContentCancellationTestSupport.ProblemAsync(excess, HttpStatusCode.Conflict, "quantity_exceeds_eligible", token);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(3, work.TotalQuantity);
        Assert.Equal(0, work.PendingQuantity);
        Assert.Equal(1, work.InPreparationQuantity);
        Assert.Equal(2, work.ReadyQuantity);
        Assert.Equal(1, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
        await ContentCancellationTestSupport.CountsAsync(fixture, 2, token);
    }

    [Theory]
    [InlineData(-1, 0, -1, 3, 4)]
    [InlineData(1, 0, 2, 3, 1)]
    [InlineData(4, 0, 4, 3, -1)]
    public async Task Database_rejects_incoherent_history(int cancelled, int previousR, int resultingR, int previousF, int resultingF)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        db.ContentCancellationHistory.Add(new ContentCancellationHistory(new ContentCancellationResponse(
            Guid.Parse(target.OperationalReference), target.IncorporationId, target.ContentOrdinal,
            Guid.CreateVersion7(), cancelled, 3, previousR, resultingR, previousF, resultingF, DateTimeOffset.UtcNow),
            fixture.DefaultOrderOperationsActor.IdentityId));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Liquidation_first_freezes_cancellation_under_the_order_lock(bool liquidationFirst)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 3, token);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, token);
        Task<HttpResponseMessage> Cancel() => ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        Task<HttpResponseMessage> Liquidate() => LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), token);
        var first = liquidationFirst ? Liquidate() : Cancel();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
        var second = liquidationFirst ? Cancel() : Liquidate();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), token));
        await blocker.CommitAsync(token);
        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal(HttpStatusCode.OK, (liquidationFirst ? firstResponse : secondResponse).StatusCode);
        var cancellation = liquidationFirst ? secondResponse : firstResponse;
        await DeliveryQuantityTestSupport.AssertProblemAsync(cancellation, HttpStatusCode.Conflict,
            liquidationFirst ? "order_operations.order.frozen" : "order_operations.content_cancellation.quantity_exceeds_eligible", token);
        await ContentCancellationTestSupport.CountsAsync(fixture, 0, token);
    }
}
