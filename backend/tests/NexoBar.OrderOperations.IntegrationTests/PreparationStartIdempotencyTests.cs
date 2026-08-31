using System.Net;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PreparationStartIdempotencyTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Same_actor_key_and_intent_replays_without_second_effect()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var key = Guid.NewGuid();

        using var firstResponse = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        var first = await PreparationStartTestSupport.ReadSuccessAsync(firstResponse, token);
        using var replayResponse = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        var replay = await PreparationStartTestSupport.ReadSuccessAsync(replayResponse, token);

        Assert.Equal(first, replay);
        await AssertSingleEffectAsync(pending: 3, inPreparation: 2, token);
    }

    [Fact]
    public async Task Replay_returns_original_result_after_later_progress()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var firstKey = Guid.NewGuid();

        using var firstResponse = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, firstKey, 2, token);
        var first = await PreparationStartTestSupport.ReadSuccessAsync(firstResponse, token);
        using var laterResponse = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, Guid.NewGuid(), 1, token);
        await PreparationStartTestSupport.ReadSuccessAsync(laterResponse, token);
        using var replayResponse = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, firstKey, 2, token);
        var replay = await PreparationStartTestSupport.ReadSuccessAsync(replayResponse, token);

        Assert.Equal(first, replay);
        Assert.Equal(3, first.PendingQuantity);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(2, work.PendingQuantity);
        Assert.Equal(3, work.InPreparationQuantity);
        Assert.Equal(2, (await fixture.ReadPreparationHistoryAsync(token)).Count);
        Assert.Equal(2, (await fixture.ReadPreparationCommandsAsync(token)).Count);
    }

    [Fact]
    public async Task Another_session_for_same_identity_can_replay()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var firstClient = scenario.Client;
        using var secondClient = await fixture.LoginAsync(scenario.Actor, token);
        var key = Guid.NewGuid();

        using var firstResponse = await PreparationStartTestSupport.PostAsync(
            firstClient, scenario.Work.Id, key, 2, token);
        var first = await PreparationStartTestSupport.ReadSuccessAsync(firstResponse, token);
        using var replayResponse = await PreparationStartTestSupport.PostAsync(
            secondClient, scenario.Work.Id, key, 2, token);
        var replay = await PreparationStartTestSupport.ReadSuccessAsync(replayResponse, token);

        Assert.Equal(first, replay);
        await AssertSingleEffectAsync(3, 2, token);
    }

    [Fact]
    public async Task Preparation_revoked_after_success_does_not_block_replay()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var key = Guid.NewGuid();
        using var firstResponse = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        var first = await PreparationStartTestSupport.ReadSuccessAsync(firstResponse, token);
        await fixture.RevokePreparationAssignmentAsync(scenario.Actor.IdentityId, token);

        using var replayResponse = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        var replay = await PreparationStartTestSupport.ReadSuccessAsync(replayResponse, token);

        Assert.Equal(first, replay);
        await AssertSingleEffectAsync(3, 2, token);
    }

    [Fact]
    public async Task Enablement_revoked_after_success_does_not_block_replay()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var key = Guid.NewGuid();
        using var firstResponse = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        var first = await PreparationStartTestSupport.ReadSuccessAsync(firstResponse, token);
        await fixture.RevokePreparationEnablementAsync(
            scenario.Actor.IdentityId,
            scenario.ResponsibilityId,
            token);

        using var replayResponse = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        var replay = await PreparationStartTestSupport.ReadSuccessAsync(replayResponse, token);

        Assert.Equal(first, replay);
        await AssertSingleEffectAsync(3, 2, token);
    }

    [Fact]
    public async Task Same_key_from_other_identity_is_conflict_before_capability_check()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var firstClient = scenario.Client;
        var key = Guid.NewGuid();
        using var firstResponse = await PreparationStartTestSupport.PostAsync(
            firstClient, scenario.Work.Id, key, 2, token);
        await PreparationStartTestSupport.ReadSuccessAsync(firstResponse, token);
        var otherActor = await fixture.CreatePreparationActorAsync(false, null, token);
        using var otherClient = await fixture.LoginAsync(otherActor, token);

        using var conflict = await PreparationStartTestSupport.PostAsync(
            otherClient, scenario.Work.Id, key, 2, token);

        await AssertConflictAsync(conflict, token);
        await AssertSingleEffectAsync(3, 2, token);
    }

    [Fact]
    public async Task Same_identity_and_key_with_different_quantity_is_conflict()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var key = Guid.NewGuid();
        using var firstResponse = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        await PreparationStartTestSupport.ReadSuccessAsync(firstResponse, token);

        using var conflict = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, key, 1, token);

        await AssertConflictAsync(conflict, token);
        await AssertSingleEffectAsync(3, 2, token);
    }

    [Fact]
    public async Task Same_identity_and_key_with_different_work_is_conflict()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        await fixture.CreatePreparedWorkAsync(
            scenario.ResponsibilityId,
            token,
            quantity: 5,
            productName: "Preparado dos");
        var otherWork = (await fixture.ReadPreparationWorkAsync(token))
            .Single(work => work.Id != scenario.Work.Id);
        var key = Guid.NewGuid();
        using var firstResponse = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        await PreparationStartTestSupport.ReadSuccessAsync(firstResponse, token);

        using var conflict = await PreparationStartTestSupport.PostAsync(
            client, otherWork.Id, key, 2, token);

        await AssertConflictAsync(conflict, token);
        var works = await fixture.ReadPreparationWorkAsync(token);
        Assert.Equal(3, works.Single(work => work.Id == scenario.Work.Id).PendingQuantity);
        Assert.Equal(5, works.Single(work => work.Id == otherWork.Id).PendingQuantity);
        Assert.Single(await fixture.ReadPreparationHistoryAsync(token));
        Assert.Single(await fixture.ReadPreparationCommandsAsync(token));
    }

    [Fact]
    public async Task Replay_survives_application_restart()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        var key = Guid.NewGuid();
        using (scenario.Client)
        {
            using var firstResponse = await PreparationStartTestSupport.PostAsync(
                scenario.Client, scenario.Work.Id, key, 2, token);
            await PreparationStartTestSupport.ReadSuccessAsync(firstResponse, token);
        }

        await fixture.RestartApplicationAsync(token);
        using var client = await fixture.LoginAsync(scenario.Actor, token);
        using var replayResponse = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        var replay = await PreparationStartTestSupport.ReadSuccessAsync(replayResponse, token);

        Assert.Equal(3, replay.PendingQuantity);
        Assert.Equal(2, replay.InPreparationQuantity);
        await AssertSingleEffectAsync(3, 2, token);
    }

    private async Task<StartScenario> CreateScenarioAsync(CancellationToken token)
    {
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity: 5);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var actor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        return new StartScenario(
            responsibility,
            actor,
            await fixture.LoginAsync(actor, token),
            work);
    }

    private async Task AssertSingleEffectAsync(
        int pending,
        int inPreparation,
        CancellationToken token)
    {
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(pending, work.PendingQuantity);
        Assert.Equal(inPreparation, work.InPreparationQuantity);
        Assert.Single(await fixture.ReadPreparationHistoryAsync(token));
        Assert.Single(await fixture.ReadPreparationCommandsAsync(token));
    }

    private static Task AssertConflictAsync(
        HttpResponseMessage response,
        CancellationToken token) =>
        PreparationStartTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.preparation_start.idempotency_key_conflict",
            token);

    private sealed record StartScenario(
        Guid ResponsibilityId,
        PreparationActor Actor,
        HttpClient Client,
        PreparationWorkSnapshot Work);
}
