using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed partial class CompleteCancellationTests(OrderOperationsApiFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Path(string order) => $"/api/orders/{order}/complete-cancellation";
    private static async Task<HttpResponseMessage> Post(HttpClient client, string order, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(order));
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);
    }
    private static async Task<CompleteCancellationResponse> Success(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<CompleteCancellationResponse>(Token))!;
    }
    private async Task<CompleteCancellationEvaluation> Read(string order)
    {
        using var response = await fixture.OrderOperationsClient.GetAsync(Path(order), Token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CompleteCancellationEvaluation>(Token))!;
    }
    private async Task GrantIntervention()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        db.ResponsibilityAssignments.Add(new(fixture.DefaultOrderOperationsActor.IdentityId, FunctionalResponsibility.OperationalIntervention));
        await db.SaveChangesAsync(Token);
    }
    private async Task<DeliveryTarget> Setup(bool prepared = false, int started = 0, int ready = 0)
    {
        await fixture.ResetAsync(Token);
        var target = prepared ? await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 7, 0, Token)
            : await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 7, Token);
        if (prepared && started > 0)
        {
            var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
            using var client = await fixture.LoginAsync(await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, Token), Token);
            using var start = await PreparationStartTestSupport.PostAsync(client, work.Id, Guid.NewGuid(), started, Token);
            start.EnsureSuccessStatusCode();
            if (ready > 0)
            {
                using var response = await PreparationReadyTestSupport.PostAsync(client, work.Id, Guid.NewGuid(), ready, Token);
                response.EnsureSuccessStatusCode();
            }
        }
        return target;
    }
    private static string PreparationHistorySnapshot(IEnumerable<PreparationHistory> facts) => JsonSerializer.Serialize(facts.Select(x => new
    { x.Id, x.WorkId, x.EventKind, x.Quantity, x.ActorIdentityId, x.OccurredAt, x.ResultingTotalQuantity,
        x.ResultingPendingQuantity, x.ResultingInPreparationQuantity, x.ResultingReadyQuantity }));
    private async Task Counts(int count)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Equal(count, await db.OrderCancellationStates.CountAsync(Token));
        Assert.Equal(count, await db.CompleteCancellationHistory.CountAsync(Token));
        Assert.Equal(count, await db.CompleteCancellationCommands.CountAsync(Token));
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 5, 0)]
    [InlineData(true, 5, 5)]
    [InlineData(true, 5, 3)]
    public async Task Complete_plan_preserves_Q_R_and_original_history_and_discards_pending(bool prepared, int started, int ready)
    {
        var target = await Setup(prepared, started, ready);
        using var correction = await ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, Token);
        correction.EnsureSuccessStatusCode();
        using var cancellation = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, Token);
        cancellation.EnsureSuccessStatusCode();
        await fixture.StartPendingCompositionAsync(target.OperationalReference, Token);
        var original = PreparationHistorySnapshot(await fixture.ReadPreparationHistoryAsync(Token));
        var contents = await fixture.ReadConfirmedContentsAsync(Token);
        var evaluation = await Read(target.OperationalReference);
        Assert.True(evaluation.IsEligible);
        Assert.Equal(started > 0, evaluation.RequiresOperationalIntervention);
        Assert.Equal(5, evaluation.RemainingFulfillmentQuantity);
        if (started > 0) await GrantIntervention();
        using var response = await Post(fixture.OrderOperationsClient, target.OperationalReference);
        var result = await Success(response);
        Assert.True(result.IsCompletelyCancelled);
        Assert.True(result.PendingCompositionDiscarded);
        Assert.Equal(TimeSpan.Zero, result.OccurredAt.Offset);
        var consequence = Assert.Single(result.Consequences);
        Assert.Equal((target.IncorporationId, target.ContentOrdinal, 5 - started, started - ready, ready, 0),
            (consequence.IncorporationId, consequence.ContentOrdinal, consequence.DirectOrPendingQuantity,
                consequence.InPreparationQuantity, consequence.ReadyQuantity, consequence.ResultingFulfillmentQuantity));
        Assert.Equal(contents, await fixture.ReadConfirmedContentsAsync(Token));
        Assert.Equal(original, PreparationHistorySnapshot(await fixture.ReadPreparationHistoryAsync(Token)));
        var quantity = Assert.Single(await fixture.ReadContentQuantityStatesAsync(Token));
        Assert.Equal((1, 6), (quantity.RemovedByCorrectionQuantity, quantity.CancelledQuantity));
        Assert.All(await fixture.ReadPreparationWorkAsync(Token), w => Assert.Equal((0, 0, 0, 0), (w.PendingQuantity, w.InPreparationQuantity, w.ReadyQuantity, w.TotalQuantity)));
        Assert.All(await fixture.ReadDeliveryStatesAsync(Token), d => Assert.Equal(0, d.DeliveredQuantity));
        await Counts(1);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Empty(await db.PendingCompositions.ToArrayAsync(Token));
        Assert.Empty(await db.Liquidations.ToArrayAsync(Token));
        Assert.Empty(await db.Closures.ToArrayAsync(Token));
        var fact = await db.CompleteCancellationHistory.SingleAsync(Token);
        Assert.Equal((result.CancellationId, result.OrderId, fixture.DefaultOrderOperationsActor.IdentityId, result.OccurredAt),
            (fact.Id, fact.OrderId, fact.ActorIdentityId, fact.OccurredAt));
        Assert.Equal("OrderCompletelyCancelled", fact.EventKind);
        Assert.Equal(consequence, (await db.CompleteCancellationDetails.SingleAsync(Token)).ToResponse());
        Assert.Single(await db.ContentCancellationHistory.ToArrayAsync(Token));
        Assert.Equal(1, await db.PendingCompositionCommands.CountAsync(Token));
        // Persistence assertions above verify the terminal result; active reads grant no History.
        using var lookup = await fixture.OrderOperationsClient.GetAsync($"/api/order-operations/orders/{target.OperationalReference}", Token);
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
        using var terminal = await fixture.OrderOperationsClient.GetAsync(Path(target.OperationalReference), Token);
        Assert.Equal(HttpStatusCode.NotFound, terminal.StatusCode);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Already_zero_open_order_has_only_terminal_fact(bool prepared)
    {
        var target = await Setup(prepared);
        using var partial = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 7, Token);
        partial.EnsureSuccessStatusCode();
        Assert.True((await Read(target.OperationalReference)).IsEligible);
        using var response = await Post(fixture.OrderOperationsClient, target.OperationalReference);
        Assert.Empty((await Success(response)).Consequences);
        await Counts(1);
        await using var scope = fixture.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>().CompleteCancellationDetails.ToArrayAsync(Token));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Effective_delivery_blocks_but_corrected_historical_delivery_does_not(bool corrected)
    {
        var target = await Setup(true, 5, 3);
        await GrantIntervention();
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 2, Token);
        var history = (await fixture.ReadDeliveryHistoryAsync(Token)).Select(x => (x.Id, x.Quantity, x.ActorIdentityId, x.OccurredAt)).ToArray();
        if (corrected)
        {
            using var correction = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 2, Token);
            correction.EnsureSuccessStatusCode();
        }
        Assert.Equal(!corrected, (await Read(target.OperationalReference)).HasEffectiveDelivery);
        var before = await fixture.ReadPreparationWorkAsync(Token);
        using var response = await Post(fixture.OrderOperationsClient, target.OperationalReference);
        Assert.Equal(corrected ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(history, (await fixture.ReadDeliveryHistoryAsync(Token)).Select(x => (x.Id, x.Quantity, x.ActorIdentityId, x.OccurredAt)).ToArray());
        if (!corrected) Assert.Equal(before, await fixture.ReadPreparationWorkAsync(Token));
        await Counts(corrected ? 1 : 0);
    }

    [Theory]
    [InlineData(0, false, true)] [InlineData(0, true, false)]
    [InlineData(2, false, true)] [InlineData(2, true, false)] [InlineData(2, true, true)]
    public async Task Authorization_is_based_on_current_plan_and_requires_both_scopes_for_real_work(int started, bool hasIntervention, bool hasOperations)
    {
        var target = await Setup(true, started);
        var actor = await fixture.CreateDeliveryActorAsync(hasOperations, false, null, Token);
        if (hasIntervention)
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
            db.ResponsibilityAssignments.Add(new(actor.IdentityId, FunctionalResponsibility.OperationalIntervention));
            await db.SaveChangesAsync(Token);
        }
        using var client = await fixture.LoginAsync(actor, Token);
        using var read = await client.GetAsync(Path(target.OperationalReference), Token);
        Assert.Equal(hasOperations ? HttpStatusCode.OK : HttpStatusCode.Forbidden, read.StatusCode);
        using var response = await Post(client, target.OperationalReference);
        var allowed = hasOperations && (started == 0 || hasIntervention);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        await Counts(allowed ? 1 : 0);
    }

    [Fact]
    public async Task Exact_replay_survives_capability_revocation_but_actor_order_and_new_key_conflict()
    {
        var target = await Setup(true, 5, 3);
        await GrantIntervention();
        var key = Guid.NewGuid();
        using var first = await Post(fixture.OrderOperationsClient, target.OperationalReference, key);
        var result = await Success(first);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
            await db.ResponsibilityAssignments.Where(x => x.IdentityId == fixture.DefaultOrderOperationsActor.IdentityId).ExecuteDeleteAsync(Token);
        }
        using var replay = await Post(fixture.OrderOperationsClient, target.OperationalReference, key);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(await Success(replay)));
        using var mismatch = await Post(fixture.OrderOperationsClient, Guid.NewGuid().ToString(), key);
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        using var actor = await fixture.LoginAsync(await fixture.CreateDeliveryActorAsync(true, false, null, Token), Token);
        using var wrongActor = await Post(actor, target.OperationalReference, key);
        Assert.Equal(HttpStatusCode.Conflict, wrongActor.StatusCode);
        using var newIntent = await Post(actor, target.OperationalReference);
        Assert.Equal(HttpStatusCode.Conflict, newIntent.StatusCode);
        await Counts(1);
    }
}
