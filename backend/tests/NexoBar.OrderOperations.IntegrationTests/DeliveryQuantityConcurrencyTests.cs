using System.Data;
using System.Data.Common;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class DeliveryQuantityConcurrencyTests(OrderOperationsApiFixture fixture)
{
    [Theory]
    [InlineData(false, 5, 2, 2, 4)]
    [InlineData(false, 3, 2, 1, 2)]
    [InlineData(true, 4, 2, 2, 4)]
    [InlineData(true, 3, 2, 1, 2)]
    public async Task Concurrent_delivery_serializes_without_overdelivery(
        bool prepared,
        int available,
        int requested,
        int expectedSuccesses,
        int expectedDelivered)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = prepared
            ? await DeliveryQuantityTestSupport.CreatePreparedAsync(
                fixture, available, available, token)
            : await DeliveryQuantityTestSupport.CreateDirectAsync(
                fixture, available, token);
        var firstActor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        var secondActor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var firstClient = await fixture.LoginAsync(firstActor, token);
        using var secondClient = await fixture.LoginAsync(secondActor, token);

        var responses = await Task.WhenAll(
            DeliveryQuantityTestSupport.PostAsync(
                firstClient,
                target.IncorporationId,
                target.ContentOrdinal,
                Guid.NewGuid(),
                requested,
                token),
            DeliveryQuantityTestSupport.PostAsync(
                secondClient,
                target.IncorporationId,
                target.ContentOrdinal,
                Guid.NewGuid(),
                requested,
                token));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        Assert.Equal(
            expectedSuccesses,
            responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        if (expectedSuccesses == 1)
        {
            var conflict = Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.Conflict);
            await DeliveryQuantityTestSupport.AssertProblemAsync(
                conflict,
                HttpStatusCode.Conflict,
                "order_operations.delivery.deliverable_quantity_insufficient",
                token);
        }

        Assert.Equal(expectedDelivered, Assert.Single(
            await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
        Assert.Equal(expectedSuccesses, (await fixture.ReadDeliveryHistoryAsync(token)).Count);
        Assert.Equal(expectedSuccesses, (await fixture.ReadDeliveryCommandsAsync(token)).Count);
        if (prepared)
        {
            Assert.Equal(available, Assert.Single(
                await fixture.ReadPreparationWorkAsync(token)).ReadyQuantity);
        }
    }

    [Fact]
    public async Task Ready_order_lock_first_makes_delivery_wait_then_observe_new_ready()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(
            fixture, 2, ready: 0, token);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var preparer = await fixture.CreatePreparationActorAsync(
            true, work.PreparationResponsibilityId, token);
        using (var startClient = await fixture.LoginAsync(preparer, token))
        using (var start = await PreparationStartTestSupport.PostAsync(
                   startClient, work.Id, Guid.NewGuid(), 1, token))
        {
            await PreparationStartTestSupport.ReadSuccessAsync(start, token);
        }

        var workLocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var readyApplication = fixture.CreateApplicationWithCapabilityDecorator(
            _ => new WorkLockingPreparationCapabilityStabilizer(
                work.Id,
                workLocked,
                releaseReady));
        using var readyClient = await fixture.LoginAsync(preparer, token, readyApplication);
        var deliverer = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var deliveryClient = await fixture.LoginAsync(deliverer, token);

        var readyTask = PreparationReadyTestSupport.PostAsync(
            readyClient, work.Id, Guid.NewGuid(), 1, token);
        await workLocked.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var deliveryTask = DeliveryQuantityTestSupport.PostAsync(
            deliveryClient,
            target.IncorporationId,
            target.ContentOrdinal,
            Guid.NewGuid(),
            1,
            token);
        try
        {
            Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(
                1, TimeSpan.FromSeconds(10), token));
            Assert.False(deliveryTask.IsCompleted);

            releaseReady.TrySetResult();
            using var readyResponse = await readyTask;
            await PreparationReadyTestSupport.ReadSuccessAsync(readyResponse, token);
            using var deliveryResponse = await deliveryTask;
            var delivered = await DeliveryQuantityTestSupport.ReadSuccessAsync(
                deliveryResponse, token);
            Assert.Equal(1, delivered.DeliveredQuantity);
            var persistedWork = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
            Assert.Equal(1, persistedWork.ReadyQuantity);
        }
        finally
        {
            releaseReady.TrySetResult();
            await ObserveAsync(readyTask);
            await ObserveAsync(deliveryTask);
        }
    }

    [Fact]
    public async Task Stabilized_responsibility_lets_delivery_commit_before_revocation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        var capabilityLocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelivery = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithOrderOperationsCapability(
            services => new BlockingOrderOperationsCapabilityStabilizer(
                services.GetRequiredService<OrderOperationsAuthorization>(),
                capabilityLocked,
                releaseDelivery));
        using var client = await fixture.LoginAsync(actor, token, application);

        var deliveryTask = DeliveryQuantityTestSupport.PostAsync(
            client,
            target.IncorporationId,
            target.ContentOrdinal,
            Guid.NewGuid(),
            1,
            token);
        await capabilityLocked.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var revokeTask = fixture.RevokeOrderOperationsAssignmentAsync(actor.IdentityId, token);
        try
        {
            Assert.True(await fixture.WaitForIdentityMutationLockAsync(
                "responsibility_assignments", TimeSpan.FromSeconds(10), token));
            Assert.False(revokeTask.IsCompleted);

            releaseDelivery.TrySetResult();
            using var response = await deliveryTask;
            await DeliveryQuantityTestSupport.ReadSuccessAsync(response, token);
            await revokeTask;

            using var afterRevocation = await DeliveryQuantityTestSupport.PostAsync(
                client,
                target.IncorporationId,
                target.ContentOrdinal,
                Guid.NewGuid(),
                1,
                token);
            await DeliveryQuantityTestSupport.AssertProblemAsync(
                afterRevocation,
                HttpStatusCode.Forbidden,
                "order_operations.delivery.forbidden",
                token);
        }
        finally
        {
            releaseDelivery.TrySetResult();
            await ObserveAsync(deliveryTask);
            await ObserveAsync(revokeTask);
        }
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
}

internal sealed class WorkLockingPreparationCapabilityStabilizer(
    Guid workId,
    TaskCompletionSource workLocked,
    TaskCompletionSource releaseReady) : IPreparationCapabilityStabilizer
{
    public Task<bool> StabilizePreparationResponsibilityAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        PreparationAuthorization.HasPreparationResponsibilityAsync(
            identityId, transaction, cancellationToken);

    public async Task<bool> StabilizeExactEnablementAsync(
        Guid identityId,
        Guid preparationResponsibilityId,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await PreparationAuthorization.HasExactEnablementAsync(
                identityId,
                preparationResponsibilityId,
                transaction,
                cancellationToken))
        {
            return false;
        }

        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id
            FROM order_operations.preparation_work
            WHERE id = @work_id
            FOR UPDATE
            """;
        AddParameter(command, "work_id", workId);
        await command.ExecuteScalarAsync(cancellationToken);
        workLocked.TrySetResult();
        await releaseReady.Task.WaitAsync(cancellationToken);
        return true;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

internal sealed class BlockingOrderOperationsCapabilityStabilizer(
    IOrderOperationsCapabilityStabilizer inner,
    TaskCompletionSource capabilityLocked,
    TaskCompletionSource releaseDelivery) : IOrderOperationsCapabilityStabilizer
{
    public async Task<bool> StabilizeResponsibilityAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var authorized = await inner.StabilizeResponsibilityAsync(
            identityId, transaction, cancellationToken);
        if (authorized)
        {
            capabilityLocked.TrySetResult();
            await releaseDelivery.Task.WaitAsync(cancellationToken);
        }

        return authorized;
    }
}
