using System.Net;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PreparationReadyIdempotencyTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Exact_replay_has_one_effect_and_returns_original_result_after_later_progress()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var key = Guid.NewGuid();

        using var firstResponse = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        var first = await PreparationReadyTestSupport.ReadSuccessAsync(firstResponse, token);
        using (var laterResponse = await PreparationReadyTestSupport.PostAsync(
                   client, scenario.Work.Id, Guid.NewGuid(), 1, token))
        {
            await PreparationReadyTestSupport.ReadSuccessAsync(laterResponse, token);
        }
        using var replayResponse = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        var replay = await PreparationReadyTestSupport.ReadSuccessAsync(replayResponse, token);

        Assert.Equal(first, replay);
        Assert.Equal(2, first.ReadyQuantity);
        await AssertEffectsAsync(1, 3, token);
    }

    [Fact]
    public async Task Another_session_for_same_identity_can_replay()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var firstClient = scenario.Client;
        using var secondClient = await fixture.LoginAsync(scenario.Actor, token);
        var key = Guid.NewGuid();

        using var firstResponse = await PreparationReadyTestSupport.PostAsync(
            firstClient, scenario.Work.Id, key, 2, token);
        var first = await PreparationReadyTestSupport.ReadSuccessAsync(firstResponse, token);
        using var replayResponse = await PreparationReadyTestSupport.PostAsync(
            secondClient, scenario.Work.Id, key, 2, token);
        var replay = await PreparationReadyTestSupport.ReadSuccessAsync(replayResponse, token);

        Assert.Equal(first, replay);
        await AssertEffectsAsync(2, 2, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capability_revoked_after_success_does_not_block_replay(
        bool revokeEnablement)
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var key = Guid.NewGuid();
        using var firstResponse = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        var first = await PreparationReadyTestSupport.ReadSuccessAsync(firstResponse, token);
        if (revokeEnablement)
        {
            await fixture.RevokePreparationEnablementAsync(
                scenario.Actor.IdentityId, scenario.ResponsibilityId, token);
        }
        else
        {
            await fixture.RevokePreparationAssignmentAsync(scenario.Actor.IdentityId, token);
        }

        using var replayResponse = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        var replay = await PreparationReadyTestSupport.ReadSuccessAsync(replayResponse, token);

        Assert.Equal(first, replay);
        await AssertEffectsAsync(2, 2, token);
    }

    [Fact]
    public async Task Same_key_from_other_identity_is_conflict_before_capability_check()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var firstClient = scenario.Client;
        var key = Guid.NewGuid();
        using var success = await PreparationReadyTestSupport.PostAsync(
            firstClient, scenario.Work.Id, key, 2, token);
        await PreparationReadyTestSupport.ReadSuccessAsync(success, token);
        var other = await fixture.CreatePreparationActorAsync(false, null, token);
        using var otherClient = await fixture.LoginAsync(other, token);

        using var conflict = await PreparationReadyTestSupport.PostAsync(
            otherClient, scenario.Work.Id, key, 2, token);

        await AssertConflictAsync(conflict, token);
        await AssertEffectsAsync(2, 2, token);
    }

    [Fact]
    public async Task Same_key_with_different_quantity_is_conflict()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var key = Guid.NewGuid();
        using var success = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        await PreparationReadyTestSupport.ReadSuccessAsync(success, token);

        using var conflict = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, key, 1, token);

        await AssertConflictAsync(conflict, token);
        await AssertEffectsAsync(2, 2, token);
    }

    [Fact]
    public async Task Same_key_with_different_work_is_conflict()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        await fixture.CreatePreparedWorkAsync(
            scenario.ResponsibilityId,
            token,
            quantity: 5,
            productName: "Otro preparado");
        var otherWork = (await fixture.ReadPreparationWorkAsync(token))
            .Single(work => work.Id != scenario.Work.Id);
        var key = Guid.NewGuid();
        using var success = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        await PreparationReadyTestSupport.ReadSuccessAsync(success, token);

        using var conflict = await PreparationReadyTestSupport.PostAsync(
            client, otherWork.Id, key, 2, token);

        await AssertConflictAsync(conflict, token);
        var original = (await fixture.ReadPreparationWorkAsync(token))
            .Single(work => work.Id == scenario.Work.Id);
        Assert.Equal(2, original.InPreparationQuantity);
        Assert.Equal(2, original.ReadyQuantity);
        var untouched = (await fixture.ReadPreparationWorkAsync(token))
            .Single(work => work.Id == otherWork.Id);
        Assert.Equal(5, untouched.PendingQuantity);
        Assert.Equal(0, untouched.InPreparationQuantity);
        Assert.Equal(0, untouched.ReadyQuantity);
    }

    [Fact]
    public async Task Start_key_cannot_be_reused_for_ready()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity: 5);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var actor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        using var client = await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();
        using var start = await PreparationStartTestSupport.PostAsync(
            client, work.Id, key, 2, token);
        await PreparationStartTestSupport.ReadSuccessAsync(start, token);

        using var conflict = await PreparationReadyTestSupport.PostAsync(
            client, work.Id, key, 2, token);

        await AssertConflictAsync(conflict, token);
        var current = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(2, current.InPreparationQuantity);
        Assert.Equal(0, current.ReadyQuantity);
    }

    [Fact]
    public async Task Ready_key_cannot_be_reused_for_start()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var key = Guid.NewGuid();
        using var ready = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);
        await PreparationReadyTestSupport.ReadSuccessAsync(ready, token);

        using var conflict = await PreparationStartTestSupport.PostAsync(
            client, scenario.Work.Id, key, 2, token);

        await PreparationStartTestSupport.AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            "order_operations.preparation_start.idempotency_key_conflict",
            token);
        await AssertEffectsAsync(2, 2, token);
    }

    private async Task<ReadyScenario> CreateScenarioAsync(CancellationToken token)
    {
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity: 5);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var actor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        var client = await fixture.LoginAsync(actor, token);
        using var start = await PreparationStartTestSupport.PostAsync(
            client, work.Id, Guid.NewGuid(), 4, token);
        await PreparationStartTestSupport.ReadSuccessAsync(start, token);
        return new ReadyScenario(responsibility, actor, client, work);
    }

    private async Task AssertEffectsAsync(
        int inPreparation,
        int ready,
        CancellationToken token)
    {
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(1, work.PendingQuantity);
        Assert.Equal(inPreparation, work.InPreparationQuantity);
        Assert.Equal(ready, work.ReadyQuantity);
        Assert.Equal(1 + ready / 2 + (ready == 3 ? 1 : 0),
            (await fixture.ReadPreparationHistoryAsync(token)).Count);
        Assert.Equal(
            (await fixture.ReadPreparationHistoryAsync(token)).Count,
            (await fixture.ReadPreparationCommandsAsync(token)).Count);
    }

    private static Task AssertConflictAsync(
        HttpResponseMessage response,
        CancellationToken token) =>
        PreparationReadyTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.preparation_ready.idempotency_key_conflict",
            token);

    private sealed record ReadyScenario(
        Guid ResponsibilityId,
        PreparationActor Actor,
        HttpClient Client,
        PreparationWorkSnapshot Work);
}
