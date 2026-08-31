using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PreparationReadyConcurrencyTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Two_workers_marking_two_from_five_both_succeed_serially()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateTwoWorkerScenarioAsync(5, token);
        using var firstClient = scenario.FirstClient;
        using var secondClient = scenario.SecondClient;

        var responses = await Task.WhenAll(
            PreparationReadyTestSupport.PostAsync(
                firstClient, scenario.Work.Id, Guid.NewGuid(), 2, token),
            PreparationReadyTestSupport.PostAsync(
                secondClient, scenario.Work.Id, Guid.NewGuid(), 2, token));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        await AssertEffectsAsync(1, 4, successfulReadyCommands: 2, token);
    }

    [Theory]
    [InlineData(5, 5, 0, 5)]
    [InlineData(3, 2, 1, 2)]
    public async Task Competing_ready_commands_cannot_over_ready(
        int started,
        int requested,
        int expectedInPreparation,
        int expectedReady)
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateTwoWorkerScenarioAsync(started, token);
        using var firstClient = scenario.FirstClient;
        using var secondClient = scenario.SecondClient;

        var responses = await Task.WhenAll(
            PreparationReadyTestSupport.PostAsync(
                firstClient, scenario.Work.Id, Guid.NewGuid(), requested, token),
            PreparationReadyTestSupport.PostAsync(
                secondClient, scenario.Work.Id, Guid.NewGuid(), requested, token));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var conflict = Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.Conflict);
        await PreparationReadyTestSupport.AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            "order_operations.preparation_ready.in_preparation_quantity_insufficient",
            token);
        await AssertEffectsAsync(
            expectedInPreparation, expectedReady, successfulReadyCommands: 1, token);
    }

    [Fact]
    public async Task Start_and_ready_on_same_work_serialize_without_deadlock()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity: 3);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var first = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        var second = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        using var firstClient = await fixture.LoginAsync(first, token);
        using var secondClient = await fixture.LoginAsync(second, token);
        using (var initialStart = await PreparationStartTestSupport.PostAsync(
                   firstClient, work.Id, Guid.NewGuid(), 1, token))
        {
            await PreparationStartTestSupport.ReadSuccessAsync(initialStart, token);
        }

        var responses = await Task.WhenAll(
            PreparationStartTestSupport.PostAsync(
                firstClient, work.Id, Guid.NewGuid(), 2, token),
            PreparationReadyTestSupport.PostAsync(
                secondClient, work.Id, Guid.NewGuid(), 1, token));
        using var startResponse = responses[0];
        using var readyResponse = responses[1];

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var current = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(0, current.PendingQuantity);
        Assert.Equal(2, current.InPreparationQuantity);
        Assert.Equal(1, current.ReadyQuantity);
    }

    [Fact]
    public async Task Positive_enablement_lock_lets_ready_commit_before_revocation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity: 2);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var actor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        using (var starter = await fixture.LoginAsync(actor, token))
        using (var start = await PreparationStartTestSupport.PostAsync(
                   starter, work.Id, Guid.NewGuid(), 2, token))
        {
            await PreparationStartTestSupport.ReadSuccessAsync(start, token);
        }

        var authorizationReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthorization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithCapabilityDecorator(
            _ => new BlockingTestPreparationCapabilityStabilizer(
                authorizationReached,
                releaseAuthorization));
        using var client = await fixture.LoginAsync(actor, token, application);

        var readyTask = PreparationReadyTestSupport.PostAsync(
            client, work.Id, Guid.NewGuid(), 1, token);
        await authorizationReached.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var revokeTask = fixture.RevokePreparationEnablementAsync(
            actor.IdentityId, responsibility, token);
        try
        {
            Assert.True(await fixture.WaitForIdentityMutationLockAsync(
                "preparation_enablements", TimeSpan.FromSeconds(10), token));
            Assert.False(revokeTask.IsCompleted);

            releaseAuthorization.TrySetResult();
            using var response = await readyTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await revokeTask;

            using var subsequent = await PreparationReadyTestSupport.PostAsync(
                client, work.Id, Guid.NewGuid(), 1, token);
            await PreparationReadyTestSupport.AssertProblemAsync(
                subsequent,
                HttpStatusCode.NotFound,
                "order_operations.preparation_work.not_found",
                token);
        }
        finally
        {
            releaseAuthorization.TrySetResult();
            await ObserveAsync(readyTask);
            await ObserveAsync(revokeTask);
        }
    }

    [Fact]
    public async Task Enablement_revocation_committed_first_forbids_new_ready()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateTwoWorkerScenarioAsync(2, token);
        using var firstClient = scenario.FirstClient;
        using var secondClient = scenario.SecondClient;
        await fixture.RevokePreparationEnablementAsync(
            scenario.FirstActor.IdentityId, scenario.ResponsibilityId, token);

        using var response = await PreparationReadyTestSupport.PostAsync(
            firstClient, scenario.Work.Id, Guid.NewGuid(), 1, token);

        await PreparationReadyTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            "order_operations.preparation_work.not_found",
            token);
        await AssertEffectsAsync(2, 0, successfulReadyCommands: 0, token);
    }

    private async Task<TwoWorkerScenario> CreateTwoWorkerScenarioAsync(
        int started,
        CancellationToken token)
    {
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity: 5);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var firstActor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        var secondActor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        var firstClient = await fixture.LoginAsync(firstActor, token);
        var secondClient = await fixture.LoginAsync(secondActor, token);
        using var start = await PreparationStartTestSupport.PostAsync(
            firstClient, work.Id, Guid.NewGuid(), started, token);
        await PreparationStartTestSupport.ReadSuccessAsync(start, token);
        return new TwoWorkerScenario(
            responsibility,
            work,
            firstActor,
            firstClient,
            secondClient);
    }

    private async Task AssertEffectsAsync(
        int inPreparation,
        int ready,
        int successfulReadyCommands,
        CancellationToken token)
    {
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(inPreparation, work.InPreparationQuantity);
        Assert.Equal(ready, work.ReadyQuantity);
        Assert.Equal(
            1 + successfulReadyCommands,
            (await fixture.ReadPreparationHistoryAsync(token)).Count);
        Assert.Equal(
            1 + successfulReadyCommands,
            (await fixture.ReadPreparationCommandsAsync(token)).Count);
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The original assertion preserves the relevant failure.
        }
    }

    private sealed record TwoWorkerScenario(
        Guid ResponsibilityId,
        PreparationWorkSnapshot Work,
        PreparationActor FirstActor,
        HttpClient FirstClient,
        HttpClient SecondClient);
}
