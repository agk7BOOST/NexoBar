using System.Net;
using System.Net.Http.Json;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PreparationCorrectionTests(OrderOperationsApiFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(DeliveryTarget Target, PreparationActor Actor, HttpClient Client)> Setup(int started = 4, int ready = 2)
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 5, 0, Token);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        var actor = await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, Token);
        var client = await fixture.LoginAsync(actor, Token);
        using var start = await PreparationStartTestSupport.PostAsync(client, work.Id, Guid.NewGuid(), started, Token);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        if (ready > 0)
        {
            using var response = await PreparationReadyTestSupport.PostAsync(client, work.Id, Guid.NewGuid(), ready, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        return (target, actor, client);
    }

    private static async Task<HttpResponseMessage> Correct(HttpClient client, DeliveryTarget target, bool ready, int quantity = 1, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/order-operations/preparation/work/{target.WorkId}/correct-{(ready ? "ready" : "start")}")
        { Content = JsonContent.Create(new { quantity }) };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);
    }

    [Theory]
    [InlineData(false, 1, 2, 1, 2)]
    [InlineData(false, 2, 3, 0, 2)]
    [InlineData(true, 1, 1, 3, 1)]
    public async Task Exact_correction_preserves_obligation_and_original_history(bool ready, int quantity, int pending, int preparing, int expectedReady)
    {
        var s = await Setup(); using var client = s.Client;
        var contents = await fixture.ReadConfirmedContentsAsync(Token);
        var state = Assert.Single(await fixture.ReadContentQuantityStatesAsync(Token));
        var originalHistory = await fixture.ReadPreparationHistoryAsync(Token);
        var originalCommands = await fixture.ReadPreparationCommandsAsync(Token);
        var other = await fixture.CreatePreparationActorAsync(true,
            Assert.Single(await fixture.ReadPreparationWorkAsync(Token)).PreparationResponsibilityId, Token);
        using var corrector = await fixture.LoginAsync(other, Token);
        using var response = await Correct(corrector, s.Target, ready, quantity);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<PreparationCorrectionResponse>(Token))!;
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        Assert.Equal((5, pending, preparing, expectedReady), (work.TotalQuantity, work.PendingQuantity, work.InPreparationQuantity, work.ReadyQuantity));
        Assert.Equal((pending, preparing, expectedReady), (result.PendingQuantity, result.InPreparationQuantity, result.ReadyQuantity));
        Assert.Equal(contents, await fixture.ReadConfirmedContentsAsync(Token));
        var after = Assert.Single(await fixture.ReadContentQuantityStatesAsync(Token));
        Assert.Equal((state.RemovedByCorrectionQuantity, state.CancelledQuantity), (after.RemovedByCorrectionQuantity, after.CancelledQuantity));
        var histories = await fixture.ReadPreparationHistoryAsync(Token);
        foreach (var h in originalHistory)
        {
            var persisted = histories.Single(x => x.Id == h.Id);
            Assert.Equal((h.WorkId, h.EventKind, h.Quantity, h.ActorIdentityId, h.OccurredAt),
                (persisted.WorkId, persisted.EventKind, persisted.Quantity, persisted.ActorIdentityId, persisted.OccurredAt));
            Assert.Equal((h.ResultingTotalQuantity, h.ResultingPendingQuantity, h.ResultingInPreparationQuantity, h.ResultingReadyQuantity),
                (persisted.ResultingTotalQuantity, persisted.ResultingPendingQuantity, persisted.ResultingInPreparationQuantity, persisted.ResultingReadyQuantity));
        }
        var correction = Assert.Single(histories, x => x.Id == result.HistoryId);
        Assert.Equal(ready ? PreparationHistory.ReadyCorrectedEventKind : PreparationHistory.StartCorrectedEventKind, correction.EventKind);
        Assert.Equal(other.IdentityId, correction.ActorIdentityId);
        Assert.Equal(quantity, correction.Quantity);
        Assert.Equal(work.Id, correction.WorkId);
        Assert.Equal(result.OccurredAt, correction.OccurredAt);
        Assert.Equal(TimeSpan.Zero, correction.OccurredAt.Offset);
        Assert.Equal((pending, preparing, expectedReady), (correction.ResultingPendingQuantity, correction.ResultingInPreparationQuantity, correction.ResultingReadyQuantity));
        foreach (var command in originalCommands)
        {
            using var replay = command.CommandKind == PreparationCommand.StartQuantityCommandKind
                ? await PreparationStartTestSupport.PostAsync(client, work.Id, command.IdempotencyKey, command.Quantity, Token)
                : await PreparationReadyTestSupport.PostAsync(client, work.Id, command.IdempotencyKey, command.Quantity, Token);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var replayResult = (await replay.Content.ReadFromJsonAsync<PreparationCorrectionResponse>(Token))!;
            Assert.Equal(command.ResultHistoryId, replayResult.HistoryId);
            Assert.Equal((command.ResultPendingQuantity, command.ResultInPreparationQuantity, command.ResultReadyQuantity), (replayResult.PendingQuantity, replayResult.InPreparationQuantity, replayResult.ReadyQuantity));
        }
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, -1)] [InlineData(false, 3)]
    [InlineData(true, 0)] [InlineData(true, -1)] [InlineData(true, 3)]
    public async Task Invalid_or_excess_quantity_has_no_effect(bool ready, int quantity)
    {
        var s = await Setup(); using var client = s.Client;
        var work = await fixture.ReadPreparationWorkAsync(Token);
        using var response = await Correct(client, s.Target, ready, quantity);
        Assert.Equal(quantity <= 0 ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(work, await fixture.ReadPreparationWorkAsync(Token));
        Assert.Equal(2, (await fixture.ReadPreparationHistoryAsync(Token)).Count);
        Assert.Equal(2, (await fixture.ReadPreparationCommandsAsync(Token)).Count);
    }

    [Fact]
    public async Task Ready_stops_at_effective_delivery_boundary()
    {
        var s = await Setup(5, 4); using var client = s.Client;
        using var deliver = await DeliveryQuantityTestSupport.PostAsync(fixture.OrderOperationsClient, s.Target.IncorporationId, s.Target.ContentOrdinal, Guid.NewGuid(), 2, Token);
        Assert.Equal(HttpStatusCode.OK, deliver.StatusCode);
        using var allowed = await Correct(client, s.Target, true, 2);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var rejected = await Correct(client, s.Target, true);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        Assert.Equal((0, 3, 2, 5), (work.PendingQuantity, work.InPreparationQuantity, work.ReadyQuantity, work.TotalQuantity));
        Assert.Equal(2, Assert.Single(await fixture.ReadDeliveryStatesAsync(Token)).DeliveredQuantity);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Replay_retains_original_result_after_later_change_and_revocation(bool ready)
    {
        var s = await Setup(); using var client = s.Client; var key = Guid.NewGuid();
        using var first = await Correct(client, s.Target, ready, key: key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var original = await first.Content.ReadAsStringAsync(Token);
        using var later = await Correct(client, s.Target, ready);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        await fixture.RevokePreparationAssignmentAsync(s.Actor.IdentityId, Token);
        using var replay = await Correct(client, s.Target, ready, key: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync(Token));
        using var forbidden = await Correct(client, s.Target, ready);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(4, (await fixture.ReadPreparationHistoryAsync(Token)).Count);
        Assert.Equal(4, (await fixture.ReadPreparationCommandsAsync(Token)).Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Wrong_destination_cannot_correct(bool ready)
    {
        var s = await Setup(); using var client = s.Client;
        var other = await fixture.CreatePreparationActorAsync(true, Guid.CreateVersion7(), Token);
        using var wrong = await fixture.LoginAsync(other, Token);
        using var response = await Correct(wrong, s.Target, ready);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(2, (await fixture.ReadPreparationHistoryAsync(Token)).Count);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Persistence_failure_rolls_back_all_effects(bool ready, bool historyFailure)
    {
        var s = await Setup(); using var client = s.Client;
        var before = await fixture.ReadPreparationWorkAsync(Token);
        if (historyFailure) await fixture.SetPreparationHistoryFailureAsync(true, Token);
        else await fixture.SetPreparationCommandFailureAsync(true, Token);
        var key = Guid.NewGuid();
        try
        {
            using var failed = await Correct(client, s.Target, ready, key: key);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(before, await fixture.ReadPreparationWorkAsync(Token));
            Assert.Equal(2, (await fixture.ReadPreparationHistoryAsync(Token)).Count);
            Assert.Equal(2, (await fixture.ReadPreparationCommandsAsync(Token)).Count);
        }
        finally
        {
            if (historyFailure) await fixture.SetPreparationHistoryFailureAsync(false, Token);
            else await fixture.SetPreparationCommandFailureAsync(false, Token);
        }
        using var retry = await Correct(client, s.Target, ready, key: key);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Theory]
    [InlineData("start", false)] [InlineData("start", true)]
    [InlineData("ready-start", false)] [InlineData("ready-start", true)]
    [InlineData("ready", false)] [InlineData("ready", true)]
    [InlineData("delivery", false)] [InlineData("delivery", true)]
    public async Task Races_revalidate_exact_quantities_after_order_lock(string competing, bool correctionFirst)
    {
        var s = await Setup(); using var client = s.Client;
        bool readyCorrection = competing is "ready" or "delivery";
        Task<HttpResponseMessage> Correction() => Correct(client, s.Target, readyCorrection, 2);
        Task<HttpResponseMessage> Compete() => competing switch
        {
            "start" => PreparationStartTestSupport.PostAsync(client, s.Target.WorkId!.Value, Guid.NewGuid(), 3, Token),
            "delivery" => DeliveryQuantityTestSupport.PostAsync(fixture.OrderOperationsClient, s.Target.IncorporationId, s.Target.ContentOrdinal, Guid.NewGuid(), 2, Token),
            _ => PreparationReadyTestSupport.PostAsync(client, s.Target.WorkId!.Value, Guid.NewGuid(), 2, Token)
        };
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, Token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, s.Target.OperationalReference, Token);
        var first = correctionFirst ? Correction() : Compete();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), Token));
        var second = correctionFirst ? Compete() : Correction();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), Token));
        await blocker.CommitAsync(Token);
        using var a = await first; using var b = await second;
        Assert.Equal(competing == "start" && !correctionFirst ? HttpStatusCode.Conflict : HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(competing is "ready-start" or "delivery" ? HttpStatusCode.Conflict : HttpStatusCode.OK, b.StatusCode);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        var expected = competing switch
        {
            "start" => correctionFirst ? (0, 3, 2) : (3, 0, 2),
            "ready-start" => correctionFirst ? (3, 0, 2) : (1, 0, 4),
            "ready" => (1, 2, 2),
            _ => correctionFirst ? (1, 4, 0) : (1, 2, 2)
        };
        Assert.Equal(expected, (work.PendingQuantity, work.InPreparationQuantity, work.ReadyQuantity));
        Assert.Equal(5, work.TotalQuantity);
        Assert.Equal(competing == "delivery" && !correctionFirst ? 2 : 0, Assert.Single(await fixture.ReadDeliveryStatesAsync(Token)).DeliveredQuantity);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Liquidation_race_and_frozen_rejection(bool ready, bool correctionFirst)
    {
        var s = await Setup(5, 5); using var client = s.Client;
        using var delivery = await DeliveryQuantityTestSupport.PostAsync(fixture.OrderOperationsClient, s.Target.IncorporationId, s.Target.ContentOrdinal, Guid.NewGuid(), 5, Token);
        Assert.Equal(HttpStatusCode.OK, delivery.StatusCode);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, Token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, s.Target.OperationalReference, Token);
        Task<HttpResponseMessage> Correction() => Correct(client, s.Target, ready);
        Task<HttpResponseMessage> Liquidate() => LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, s.Target.OperationalReference, Guid.NewGuid(), Token);
        var first = correctionFirst ? Correction() : Liquidate();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), Token));
        var second = correctionFirst ? Liquidate() : Correction();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), Token));
        await blocker.CommitAsync(Token);
        using var a = await first; using var b = await second;
        Assert.Equal(correctionFirst ? HttpStatusCode.Conflict : HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(correctionFirst ? HttpStatusCode.OK : HttpStatusCode.Conflict, b.StatusCode);
        using var frozen = await Correction();
        Assert.Equal(HttpStatusCode.Conflict, frozen.StatusCode);
        Assert.Contains("frozen", await frozen.Content.ReadAsStringAsync(Token), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, (await fixture.ReadPreparationHistoryAsync(Token)).Count);
        Assert.Equal(5, Assert.Single(await fixture.ReadPreparationWorkAsync(Token)).ReadyQuantity);
    }
}
