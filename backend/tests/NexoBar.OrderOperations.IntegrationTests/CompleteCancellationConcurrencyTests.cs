using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed partial class CompleteCancellationTests
{
    private static async Task<HttpResponseMessage> Command(HttpClient client, string path, object? body = null, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);
    }

    private Task<HttpResponseMessage> Compete(string kind, DeliveryTarget target, HttpClient preparer, Guid marker, Guid product) => kind switch
    {
        "confirmation" => Command(fixture.OrderOperationsClient, $"/api/order-operations/orders/{target.OperationalReference}/confirmations",
            new SubsequentConfirmationRequest(marker, [new(product, 2)])),
        "pending-start" => Command(fixture.OrderOperationsClient, $"/api/orders/{target.OperationalReference}/pending-composition"),
        "pending-discard" => Command(fixture.OrderOperationsClient, $"/api/orders/{target.OperationalReference}/pending-composition/{marker}/discard"),
        "content-cancellation" => ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, Token),
        "content-correction" => ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, Token),
        "start" => PreparationStartTestSupport.PostAsync(preparer, target.WorkId!.Value, Guid.NewGuid(), 1, Token),
        "ready" => PreparationReadyTestSupport.PostAsync(preparer, target.WorkId!.Value, Guid.NewGuid(), 1, Token),
        "correct-start" or "correct-ready" => Command(preparer, $"/api/order-operations/preparation/work/{target.WorkId}/{kind}", new { quantity = 1 }),
        "intervention" => Command(fixture.OrderOperationsClient, $"/api/order-operations/intervention/work/{target.WorkId}/in-preparation", new { quantity = 1 }),
        "delivery" => DeliveryQuantityTestSupport.PostAsync(fixture.OrderOperationsClient, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 1, Token),
        "delivery-correction" => DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, Token),
        "liquidation" => LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), Token),
        "closure" => ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), Token),
        _ => throw new InvalidOperationException(kind)
    };

    [Theory]
    [InlineData("confirmation")] [InlineData("pending-start")] [InlineData("pending-discard")]
    [InlineData("content-cancellation")] [InlineData("content-correction")]
    [InlineData("start")] [InlineData("ready")] [InlineData("correct-start")] [InlineData("correct-ready")]
    [InlineData("intervention")] [InlineData("delivery")] [InlineData("delivery-correction")]
    [InlineData("liquidation")] [InlineData("closure")]
    public async Task Cancellation_wins_Order_lock_and_every_ordinary_mutation_rejects_terminal(string kind)
    {
        var target = await Setup(true, 5, 3);
        await GrantIntervention();
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        using var preparer = await fixture.LoginAsync(await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, Token), Token);
        var marker = kind == "pending-start" ? Guid.NewGuid() : (await fixture.StartPendingCompositionAsync(target.OperationalReference, Token)).PendingCompositionId;
        var product = (await fixture.ReadConfirmedContentsAsync(Token)).Single().ProductId;
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, Token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, Token);
        var cancel = Post(fixture.OrderOperationsClient, target.OperationalReference);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), Token));
        var competing = Compete(kind, target, preparer, marker, product);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), Token));
        await blocker.CommitAsync(Token);
        using var cancelled = await cancel;
        await Success(cancelled);
        using var rejected = await competing;
        await DeliveryQuantityTestSupport.AssertProblemAsync(rejected, HttpStatusCode.Conflict, "order_operations.order_completely_cancelled", Token);
        await Counts(1);
    }

    [Theory]
    [InlineData("confirmation")] [InlineData("pending-start")] [InlineData("pending-discard")]
    [InlineData("content-cancellation")] [InlineData("content-correction")]
    [InlineData("start")] [InlineData("ready")] [InlineData("intervention")] [InlineData("delivery")]
    public async Task Earlier_mutation_is_included_in_stabilized_complete_plan(string kind)
    {
        var target = await Setup(true, 5, 3);
        await GrantIntervention();
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        using var preparer = await fixture.LoginAsync(await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, Token), Token);
        var marker = kind is "confirmation" or "pending-discard" ? (await fixture.StartPendingCompositionAsync(target.OperationalReference, Token)).PendingCompositionId : Guid.NewGuid();
        var product = (await fixture.ReadConfirmedContentsAsync(Token)).Single().ProductId;
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, Token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, Token);
        var first = Compete(kind, target, preparer, marker, product);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), Token));
        var second = Post(fixture.OrderOperationsClient, target.OperationalReference);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), Token));
        await blocker.CommitAsync(Token);
        using var mutation = await first;
        mutation.EnsureSuccessStatusCode();
        using var cancellation = await second;
        if (kind == "delivery")
        {
            Assert.Equal(HttpStatusCode.Conflict, cancellation.StatusCode);
            await Counts(0);
            Assert.Equal(0, Assert.Single(await fixture.ReadContentQuantityStatesAsync(Token)).CancelledQuantity);
            return;
        }
        var result = await Success(cancellation);
        Assert.Equal(kind == "pending-start", result.PendingCompositionDiscarded);
        Assert.Equal(kind == "confirmation" ? 2 : 1, result.Consequences.Count);
        Assert.Equal(kind == "confirmation" ? 9 : kind is "content-cancellation" or "content-correction" or "intervention" ? 6 : 7,
            result.Consequences.Sum(x => x.DirectOrPendingQuantity + x.InPreparationQuantity + x.ReadyQuantity));
        Assert.All(await fixture.ReadPreparationWorkAsync(Token), x => Assert.Equal(0, x.TotalQuantity));
        await Counts(1);
    }

    [Fact]
    public async Task Preparation_winning_changes_conditional_authorization_after_lock()
    {
        var target = await Setup(true);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        using var preparer = await fixture.LoginAsync(await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, Token), Token);
        Assert.False((await Read(target.OperationalReference)).RequiresOperationalIntervention);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, Token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, Token);
        var start = PreparationStartTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), 1, Token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), Token));
        var cancel = Post(fixture.OrderOperationsClient, target.OperationalReference);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), Token));
        await blocker.CommitAsync(Token);
        using var started = await start; started.EnsureSuccessStatusCode();
        using var rejected = await cancel;
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        await Counts(0);
    }

    [Fact]
    public async Task Concurrent_exact_replay_has_one_result()
    {
        var target = await Setup(); var key = Guid.NewGuid();
        var responses = await Task.WhenAll(Post(fixture.OrderOperationsClient, target.OperationalReference, key), Post(fixture.OrderOperationsClient, target.OperationalReference, key));
        using var first = responses[0]; using var second = responses[1];
        var original = await Success(first); var replay = await Success(second);
        Assert.Equal(original.CancellationId, replay.CancellationId);
        Assert.Equal(original.Consequences, replay.Consequences);
        await Counts(1);
    }
}
