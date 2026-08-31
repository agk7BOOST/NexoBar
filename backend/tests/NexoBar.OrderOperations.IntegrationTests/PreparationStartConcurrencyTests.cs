using System.Data;
using System.Data.Common;
using System.Net;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PreparationStartConcurrencyTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Two_workers_starting_two_from_five_both_succeed_serially()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateTwoWorkerScenarioAsync(5, token);
        using var firstClient = scenario.FirstClient;
        using var secondClient = scenario.SecondClient;

        var responses = await Task.WhenAll(
            PreparationStartTestSupport.PostAsync(
                firstClient, scenario.Work.Id, Guid.NewGuid(), 2, token),
            PreparationStartTestSupport.PostAsync(
                secondClient, scenario.Work.Id, Guid.NewGuid(), 2, token));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        await AssertEffectsAsync(1, 4, successfulCommands: 2, token);
        var actors = (await fixture.ReadPreparationHistoryAsync(token))
            .Select(history => history.ActorIdentityId)
            .ToHashSet();
        Assert.Contains(scenario.FirstActor.IdentityId, actors);
        Assert.Contains(scenario.SecondActor.IdentityId, actors);
    }

    [Fact]
    public async Task Two_workers_starting_all_from_five_have_exactly_one_winner()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateTwoWorkerScenarioAsync(5, token);
        using var firstClient = scenario.FirstClient;
        using var secondClient = scenario.SecondClient;

        var responses = await Task.WhenAll(
            PreparationStartTestSupport.PostAsync(
                firstClient, scenario.Work.Id, Guid.NewGuid(), 5, token),
            PreparationStartTestSupport.PostAsync(
                secondClient, scenario.Work.Id, Guid.NewGuid(), 5, token));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        await AssertOneWinnerAsync(responses, token);
        await AssertEffectsAsync(0, 5, successfulCommands: 1, token);
    }

    [Fact]
    public async Task Competing_starts_cannot_over_progress_three_pending()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateTwoWorkerScenarioAsync(3, token);
        using var firstClient = scenario.FirstClient;
        using var secondClient = scenario.SecondClient;

        var responses = await Task.WhenAll(
            PreparationStartTestSupport.PostAsync(
                firstClient, scenario.Work.Id, Guid.NewGuid(), 2, token),
            PreparationStartTestSupport.PostAsync(
                secondClient, scenario.Work.Id, Guid.NewGuid(), 2, token));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        await AssertOneWinnerAsync(responses, token);
        await AssertEffectsAsync(1, 2, successfulCommands: 1, token);
    }

    [Fact]
    public async Task Positive_enablement_lock_lets_start_commit_before_revocation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity: 2);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var actor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        var authorizationReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthorization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithCapabilityDecorator(
            _ => new BlockingTestPreparationCapabilityStabilizer(
                authorizationReached,
                releaseAuthorization));
        using var client = await fixture.LoginAsync(actor, token, application);

        var startTask = PreparationStartTestSupport.PostAsync(
            client, work.Id, Guid.NewGuid(), 1, token);
        await authorizationReached.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var revokeTask = fixture.RevokePreparationEnablementAsync(
            actor.IdentityId,
            responsibility,
            token);
        try
        {
            Assert.True(await fixture.WaitForIdentityMutationLockAsync(
                "preparation_enablements",
                TimeSpan.FromSeconds(10),
                token));
            Assert.False(revokeTask.IsCompleted);

            releaseAuthorization.TrySetResult();
            using var response = await startTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await revokeTask;

            using var subsequent = await PreparationStartTestSupport.PostAsync(
                client, work.Id, Guid.NewGuid(), 1, token);
            await PreparationStartTestSupport.AssertProblemAsync(
                subsequent,
                HttpStatusCode.NotFound,
                "order_operations.preparation_work.not_found",
                token);
            await AssertEffectsAsync(1, 1, successfulCommands: 1, token);
        }
        finally
        {
            releaseAuthorization.TrySetResult();
            await ObserveAsync(startTask);
            await ObserveAsync(revokeTask);
        }
    }

    [Fact]
    public async Task Enablement_revocation_committed_first_forbids_new_start_as_not_found()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity: 2);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var actor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        using var client = await fixture.LoginAsync(actor, token);
        await fixture.RevokePreparationEnablementAsync(
            actor.IdentityId,
            responsibility,
            token);

        using var response = await PreparationStartTestSupport.PostAsync(
            client, work.Id, Guid.NewGuid(), 1, token);

        await PreparationStartTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            "order_operations.preparation_work.not_found",
            token);
        await AssertEffectsAsync(2, 0, successfulCommands: 0, token);
    }

    private async Task<TwoWorkerScenario> CreateTwoWorkerScenarioAsync(
        int quantity,
        CancellationToken token)
    {
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var firstActor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        var secondActor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        return new TwoWorkerScenario(
            work,
            firstActor,
            secondActor,
            await fixture.LoginAsync(firstActor, token),
            await fixture.LoginAsync(secondActor, token));
    }

    private async Task AssertEffectsAsync(
        int pending,
        int inPreparation,
        int successfulCommands,
        CancellationToken token)
    {
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(pending, work.PendingQuantity);
        Assert.Equal(inPreparation, work.InPreparationQuantity);
        Assert.Equal(successfulCommands, (await fixture.ReadPreparationHistoryAsync(token)).Count);
        Assert.Equal(successfulCommands, (await fixture.ReadPreparationCommandsAsync(token)).Count);
    }

    private static async Task AssertOneWinnerAsync(
        HttpResponseMessage[] responses,
        CancellationToken token)
    {
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var conflict = Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.Conflict);
        await PreparationStartTestSupport.AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            "order_operations.preparation_start.pending_quantity_insufficient",
            token);
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
        PreparationWorkSnapshot Work,
        PreparationActor FirstActor,
        PreparationActor SecondActor,
        HttpClient FirstClient,
        HttpClient SecondClient);
}

internal sealed class BlockingTestPreparationCapabilityStabilizer(
    TaskCompletionSource authorizationReached,
    TaskCompletionSource releaseAuthorization) : IPreparationCapabilityStabilizer
{
    public Task<bool> StabilizePreparationResponsibilityAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        StabilizeAsync(
            transaction,
            """
            SELECT 1
            FROM identities_and_capabilities.responsibility_assignments
            WHERE identity_id = @identity_id
              AND responsibility_code = 'Preparation'
            FOR SHARE
            """,
            identityId,
            preparationResponsibilityId: null,
            cancellationToken);

    public async Task<bool> StabilizeExactEnablementAsync(
        Guid identityId,
        Guid preparationResponsibilityId,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = await StabilizeAsync(
            transaction,
            """
            SELECT 1
            FROM identities_and_capabilities.preparation_enablements
            WHERE identity_id = @identity_id
              AND preparation_responsibility_id = @preparation_responsibility_id
            FOR SHARE
            """,
            identityId,
            preparationResponsibilityId,
            cancellationToken);
        if (result)
        {
            authorizationReached.TrySetResult();
            await releaseAuthorization.Task.WaitAsync(cancellationToken);
        }

        return result;
    }

    private static async Task<bool> StabilizeAsync(
        DbTransaction transaction,
        string commandText,
        Guid identityId,
        Guid? preparationResponsibilityId,
        CancellationToken cancellationToken)
    {
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("Transaction connection is required.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Transaction connection must be open.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        AddParameter(command, "identity_id", identityId);
        if (preparationResponsibilityId is { } responsibilityId)
        {
            AddParameter(command, "preparation_responsibility_id", responsibilityId);
        }

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
