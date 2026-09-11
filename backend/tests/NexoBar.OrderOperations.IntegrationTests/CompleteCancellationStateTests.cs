using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed partial class CompleteCancellationTests
{
    [Theory]
    [InlineData("delivery")] [InlineData("missing-quantity")] [InlineData("missing-delivery")]
    [InlineData("missing-work")] [InlineData("inconsistent-work")] [InlineData("success")]
    public async Task Whole_Order_plan_includes_mixed_contents_and_aborts_for_any_bad_content(string condition)
    {
        var target = await Setup(true, 5, 3);
        await GrantIntervention();
        var pending = await fixture.StartPendingCompositionAsync(target.OperationalReference, Token);
        var product = await fixture.CreateProductAsync("Direct extra", "4", Token);
        var extra = await fixture.CreateProductAsync("Second direct extra", "4", Token);
        using var confirmation = await Command(fixture.OrderOperationsClient,
            $"/api/order-operations/orders/{target.OperationalReference}/confirmations",
            new SubsequentConfirmationRequest(pending.PendingCompositionId, [new(product.Id, 4), new(extra.Id, 2)]));
        confirmation.EnsureSuccessStatusCode();
        pending = await fixture.StartPendingCompositionAsync(target.OperationalReference, Token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var direct = await db.IncorporationContents.AsNoTracking().Where(x => !x.RequiresPreparationAtConfirmation).OrderBy(x => x.ContentOrdinal).LastAsync(Token);
        switch (condition)
        {
            case "delivery":
                using (var response = await DeliveryQuantityTestSupport.PostAsync(fixture.OrderOperationsClient, direct.IncorporationId, direct.ContentOrdinal, Guid.NewGuid(), 1, Token)) response.EnsureSuccessStatusCode();
                break;
            case "missing-quantity":
                await db.ContentQuantityStates.Where(x => x.IncorporationId == direct.IncorporationId && x.ContentOrdinal == direct.ContentOrdinal).ExecuteDeleteAsync(Token); break;
            case "missing-delivery":
                await db.DeliveryStates.Where(x => x.IncorporationId == direct.IncorporationId && x.ContentOrdinal == direct.ContentOrdinal).ExecuteDeleteAsync(Token); break;
            case "missing-work":
                await db.IncorporationContents.Where(x => x.IncorporationId == direct.IncorporationId && x.ContentOrdinal == direct.ContentOrdinal)
                    .ExecuteUpdateAsync(x => x.SetProperty(c => c.RequiresPreparationAtConfirmation, true), Token); break;
            case "inconsistent-work":
                await db.PreparationWork.Where(x => x.Id == target.WorkId).ExecuteUpdateAsync(x => x.SetProperty(w => w.TotalQuantity, 8).SetProperty(w => w.PendingQuantity, 3), Token); break;
        }
        var quantities = (await fixture.ReadContentQuantityStatesAsync(Token))
            .Select(x => (x.IncorporationId, x.ContentOrdinal, x.RemovedByCorrectionQuantity, x.CancelledQuantity)).ToArray();
        var works = (await fixture.ReadPreparationWorkAsync(Token))
            .Select(x => (x.Id, x.TotalQuantity, x.PendingQuantity, x.InPreparationQuantity, x.ReadyQuantity)).ToArray();
        var evaluation = await Read(target.OperationalReference);
        Assert.Equal(condition == "success", evaluation.IsEligible);
        if (condition is not ("success" or "delivery"))
        {
            Assert.Null(evaluation.RemainingFulfillmentQuantity);
            Assert.Null(evaluation.RequiresOperationalIntervention);
            Assert.Empty(evaluation.Consequences);
        }
        using var cancellation = await Post(fixture.OrderOperationsClient, target.OperationalReference);
        if (condition == "success")
        {
            var result = await Success(cancellation);
            Assert.Equal(3, result.Consequences.Count);
            Assert.Equal(8, result.Consequences.Sum(x => x.DirectOrPendingQuantity));
            Assert.Equal(2, result.Consequences.Sum(x => x.InPreparationQuantity));
            Assert.Equal(3, result.Consequences.Sum(x => x.ReadyQuantity));
            await Counts(1);
        }
        else
        {
            Assert.Equal(condition == "delivery" ? HttpStatusCode.Conflict : HttpStatusCode.InternalServerError, cancellation.StatusCode);
            Assert.Equal(quantities, (await fixture.ReadContentQuantityStatesAsync(Token))
                .Select(x => (x.IncorporationId, x.ContentOrdinal, x.RemovedByCorrectionQuantity, x.CancelledQuantity)).ToArray());
            Assert.Equal(works, (await fixture.ReadPreparationWorkAsync(Token))
                .Select(x => (x.Id, x.TotalQuantity, x.PendingQuantity, x.InPreparationQuantity, x.ReadyQuantity)).ToArray());
            Assert.Equal(pending.PendingCompositionId, (await db.PendingCompositions.SingleAsync(Token)).Id);
            await Counts(0);
            Assert.Empty(await db.CompleteCancellationDetails.ToArrayAsync(Token));
        }
    }

    [Theory]
    [InlineData("complete_cancellation_history")] [InlineData("complete_cancellation_details")]
    [InlineData("order_cancellation_states")] [InlineData("complete_cancellation_commands")]
    public async Task Persistence_failure_rolls_back_quantities_pending_history_terminal_state_and_intent(string table)
    {
        var target = await Setup(true, 5, 3);
        await GrantIntervention();
        var pending = await fixture.StartPendingCompositionAsync(target.OperationalReference, Token);
        var before = await fixture.ReadPreparationWorkAsync(Token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        // Table names come only from the fixed test cases above.
        var sql = "CREATE FUNCTION order_operations.fail_complete_cancellation() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'controlled cancellation failure'; END; $$; " +
            $"CREATE TRIGGER fail_complete_cancellation BEFORE INSERT ON order_operations.{table} FOR EACH ROW EXECUTE FUNCTION order_operations.fail_complete_cancellation();";
        await db.Database.ExecuteSqlRawAsync(sql, Token);
        var key = Guid.NewGuid();
        try
        {
            using var response = await Post(fixture.OrderOperationsClient, target.OperationalReference, key);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await Counts(0);
            Assert.Empty(await db.CompleteCancellationDetails.ToArrayAsync(Token));
            Assert.Equal(before, await fixture.ReadPreparationWorkAsync(Token));
            Assert.Equal(0, Assert.Single(await fixture.ReadContentQuantityStatesAsync(Token)).CancelledQuantity);
            Assert.Equal(pending.PendingCompositionId, (await db.PendingCompositions.SingleAsync(Token)).Id);
        }
        finally
        {
            var cleanup = $"DROP TRIGGER fail_complete_cancellation ON order_operations.{table}; DROP FUNCTION order_operations.fail_complete_cancellation();";
            await db.Database.ExecuteSqlRawAsync(cleanup, Token);
        }
        using var retry = await Post(fixture.OrderOperationsClient, target.OperationalReference, key);
        await Success(retry);
        await Counts(1);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Liquidation_or_Closure_winning_first_blocks_complete_cancellation(bool closed)
    {
        var target = await Setup();
        using var partial = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 7, Token);
        partial.EnsureSuccessStatusCode();
        if (closed)
        {
            using var liquidation = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), Token);
            liquidation.EnsureSuccessStatusCode();
        }
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, Token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, Token);
        var first = closed ? ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), Token)
            : LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), Token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), Token));
        var cancel = Post(fixture.OrderOperationsClient, target.OperationalReference);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), Token));
        await blocker.CommitAsync(Token);
        using var terminal = await first; terminal.EnsureSuccessStatusCode();
        using var rejected = await cancel;
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        await Counts(0);
    }

    [Fact]
    public async Task Prior_pending_Start_and_Discard_intents_replay_without_recreating_marker()
    {
        var target = await Setup(); var key = Guid.NewGuid(); var discardKey = Guid.NewGuid();
        var path = $"/api/orders/{target.OperationalReference}/pending-composition";
        using var start = await Command(fixture.OrderOperationsClient, path, key: key);
        var marker = (await start.Content.ReadFromJsonAsync<PendingCompositionResponse>(Token))!;
        using var discard = await Command(fixture.OrderOperationsClient, $"{path}/{marker.PendingCompositionId}/discard", key: discardKey);
        discard.EnsureSuccessStatusCode();
        using var cancel = await Post(fixture.OrderOperationsClient, target.OperationalReference); await Success(cancel);
        using var replayStart = await Command(fixture.OrderOperationsClient, path, key: key);
        Assert.Equal(marker, await replayStart.Content.ReadFromJsonAsync<PendingCompositionResponse>(Token));
        using var replayDiscard = await Command(fixture.OrderOperationsClient, $"{path}/{marker.PendingCompositionId}/discard", key: discardKey);
        Assert.Equal(discard.StatusCode, replayDiscard.StatusCode);
        Assert.Equal(0, await fixture.CountPendingCompositionsAsync(Token));
    }

    [Theory]
    [InlineData("missing-key")] [InlineData("v7-key")] [InlineData("antiforgery")]
    [InlineData("body")] [InlineData("anonymous")]
    public async Task Http_boundary_rejects_invalid_intentions(string condition)
    {
        var target = await Setup();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(target.OperationalReference));
        if (condition != "missing-key") request.Headers.Add("Idempotency-Key", (condition == "v7-key" ? Guid.CreateVersion7() : Guid.NewGuid()).ToString());
        if (condition == "body") request.Content = JsonContent.Create(new { actor = Guid.NewGuid() });
        using var response = condition is "antiforgery" or "anonymous"
            ? await (condition == "anonymous" ? fixture.Client : fixture.OrderOperationsClient).SendAsync(request, Token)
            : await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, Token);
        Assert.Equal(condition == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest, response.StatusCode);
        await Counts(0);
    }

    [Fact]
    public async Task Exact_replay_still_requires_usable_session()
    {
        var target = await Setup(); var key = Guid.NewGuid();
        using var cancel = await Post(fixture.OrderOperationsClient, target.OperationalReference, key); await Success(cancel);
        await fixture.RevokeSessionsAsync(fixture.DefaultOrderOperationsActor.IdentityId, Token);
        using var response = await Post(fixture.OrderOperationsClient, target.OperationalReference, key);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await Counts(1);
    }
}
