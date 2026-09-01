using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryCountReconciliationConcurrencyTests(
    InventoryApiFixture fixture)
{
    [Fact]
    public async Task Competing_reconciliations_from_same_revision_allow_exactly_one_movement()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, 10m, 1, token);
        var counter = await ActorAsync("counter", token);
        await fixture.LoginAsync(counter, token);
        var countA = await CountAsync(fixture.Client, item.Id, "8", token);
        var countB = await CountAsync(fixture.Client, item.Id, "9", token);
        var actorA = await ActorAsync("reconciler-a", token);
        var actorB = await ActorAsync("reconciler-b", token);
        using var clientA = fixture.CreateAnonymousClient();
        using var clientB = fixture.CreateAnonymousClient();
        await fixture.LoginAsync(clientA, actorA, token);
        await fixture.LoginAsync(clientB, actorB, token);
        var csrfA = await InventoryApiFixture.GetAntiforgeryTokenAsync(clientA, token);
        var csrfB = await InventoryApiFixture.GetAntiforgeryTokenAsync(clientB, token);

        var responses = await Task.WhenAll(
            InventoryApiFixture.PostReconcileAsync(
                clientA, item.Id, Guid.NewGuid(), countA.CountObservationId, token, csrfA),
            InventoryApiFixture.PostReconcileAsync(
                clientB, item.Id, Guid.NewGuid(), countB.CountObservationId, token, csrfB));
        try
        {
            Assert.Equal(1, responses.Count(value => value.StatusCode == HttpStatusCode.OK));
            var conflict = Assert.Single(
                responses,
                value => value.StatusCode == HttpStatusCode.Conflict);
            await InventoryTestAssertions.AssertProblemAsync(
                conflict,
                HttpStatusCode.Conflict,
                "inventory.reconciliation.count_invalidated",
                token);
            Assert.Single(await fixture.ReadMovementsAsync(token));
            Assert.Equal(2, (await fixture.ReadItemAsync(item.Id, token)).MovementRevision);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Counts_for_different_items_can_both_complete_independently()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var first = await fixture.AddItemAsync("Harina", "kg", token);
        var second = await fixture.AddItemAsync("Azucar", "bolsa", token);
        var actor = await ActorAsync("parallel-counts", token);
        await fixture.LoginAsync(actor, token);
        var csrf = await fixture.GetAntiforgeryTokenAsync(token);

        var responses = await Task.WhenAll(
            fixture.PostCountAsync(first.Id, Guid.NewGuid(), "1", token, csrf),
            fixture.PostCountAsync(second.Id, Guid.NewGuid(), "2", token, csrf));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            Assert.Equal((2, 2, 0, 0), await fixture.ReadInventoryEffectCountsAsync(token));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Movement_lock_first_makes_count_wait_then_capture_new_revision()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await ActorAsync("movement-lock", token);
        await fixture.LoginAsync(actor, token);
        var movementCount = await CountAsync(fixture.Client, item.Id, "5", token);
        var csrf = await fixture.GetAntiforgeryTokenAsync(token);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            token);
        var committed = false;
        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText =
                "SELECT id FROM inventory.inventory_items WHERE id = @id FOR UPDATE";
            lockCommand.Parameters.AddWithValue("id", item.Id);
            Assert.Equal(item.Id, await lockCommand.ExecuteScalarAsync(token));
        }

        var countTask = fixture.PostCountAsync(
            item.Id, Guid.NewGuid(), "6", token, csrf);
        try
        {
            Assert.True(await fixture.WaitForDatabaseLockAsync(
                "FROM inventory.inventory_items",
                TimeSpan.FromSeconds(10),
                token));
            Assert.False(countTask.IsCompleted);

            await using var movementCommand = connection.CreateCommand();
            movementCommand.Transaction = transaction;
            movementCommand.CommandText =
                """
                UPDATE inventory.inventory_items
                SET current_registered_quantity = 5,
                    movement_revision = 1
                WHERE id = @item_id;
                INSERT INTO inventory.inventory_movements
                    (id, inventory_item_id, movement_revision, nature, quantity,
                     previous_registered_quantity, resulting_registered_quantity,
                     count_observation_id, occurred_at, actor_identity_id)
                VALUES
                    (@movement_id, @item_id, 1, 'Reconciliation', 5,
                     NULL, 5, @count_id, now(), @actor_id);
                """;
            movementCommand.Parameters.AddWithValue("movement_id", Guid.CreateVersion7());
            movementCommand.Parameters.AddWithValue("item_id", item.Id);
            movementCommand.Parameters.AddWithValue(
                "count_id",
                movementCount.CountObservationId);
            movementCommand.Parameters.AddWithValue("actor_id", actor.IdentityId);
            await movementCommand.ExecuteNonQueryAsync(token);
            await transaction.CommitAsync(token);
            committed = true;

            using var response = await countTask;
            var captured = await response.Content
                .ReadFromJsonAsync<CountObservationResponse>(token);
            Assert.Equal(1, captured!.ObservedMovementRevision);
            Assert.Equal("kg", captured.ObservedOperationalUnit);
        }
        finally
        {
            if (!committed)
            {
                await transaction.RollbackAsync(token);
            }

            await ObserveAsync(countTask);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stabilized_inventory_operation_allows_command_before_revocation(
        bool reconcile)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await ActorAsync($"auth-{reconcile}", token);
        await fixture.LoginAsync(actor, token);
        CountObservationResponse? count = reconcile
            ? await CountAsync(fixture.Client, item.Id, "3", token)
            : null;
        var capabilityLocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommand = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithAuthorization(
            services => new BlockingInventoryAuthorization(
                new InventoryAuthorization(
                    services.GetRequiredService<IAuthenticatedSessionStabilizer>()),
                capabilityLocked,
                releaseCommand));
        using var client = application.CreateClient();
        await fixture.LoginAsync(client, actor, token);
        var csrf = await InventoryApiFixture.GetAntiforgeryTokenAsync(client, token);

        var commandTask = reconcile
            ? InventoryApiFixture.PostReconcileAsync(
                client,
                item.Id,
                Guid.NewGuid(),
                count!.CountObservationId,
                token,
                csrf)
            : InventoryApiFixture.PostCountAsync(
                client, item.Id, Guid.NewGuid(), "3", token, csrf);
        await capabilityLocked.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var revokeTask = fixture.RevokeResponsibilityAsync(
            actor.IdentityId,
            FunctionalResponsibility.InventoryOperation,
            token);
        try
        {
            Assert.True(await fixture.WaitForDatabaseLockAsync(
                "responsibility_assignments",
                TimeSpan.FromSeconds(10),
                token));
            Assert.False(revokeTask.IsCompleted);
            releaseCommand.TrySetResult();
            using var response = await commandTask;
            Assert.True(response.IsSuccessStatusCode);
            await revokeTask;

            using var after = reconcile
                ? await InventoryApiFixture.PostReconcileAsync(
                    client,
                    item.Id,
                    Guid.NewGuid(),
                    count!.CountObservationId,
                    token,
                    csrf)
                : await InventoryApiFixture.PostCountAsync(
                    client, item.Id, Guid.NewGuid(), "4", token, csrf);
            Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        }
        finally
        {
            releaseCommand.TrySetResult();
            await ObserveAsync(commandTask);
            await ObserveAsync(revokeTask);
        }
    }

    private async Task<CountObservationResponse> CountAsync(
        HttpClient client,
        Guid itemId,
        string quantity,
        CancellationToken token)
    {
        using var response = await InventoryApiFixture.PostCountAsync(
            client, itemId, Guid.NewGuid(), quantity, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CountObservationResponse>(token))!;
    }

    private Task<InventoryActor> ActorAsync(string suffix, CancellationToken token) =>
        fixture.CreateActorAsync(
            $"concurrency-{suffix}-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The originating assertion preserves the relevant failure.
        }
    }
}

internal sealed class BlockingInventoryAuthorization(
    IInventoryAuthorization inner,
    TaskCompletionSource capabilityLocked,
    TaskCompletionSource releaseCommand) : IInventoryAuthorization
{
    public Task<InventoryAuthorizedIdentity?> StabilizeSessionAndIdentityAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        inner.StabilizeSessionAndIdentityAsync(transaction, cancellationToken);

    public Task<bool> StabilizeInventoryConfigurationAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        inner.StabilizeInventoryConfigurationAsync(
            identityId, transaction, cancellationToken);

    public async Task<bool> StabilizeInventoryOperationAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var authorized = await inner.StabilizeInventoryOperationAsync(
            identityId,
            transaction,
            cancellationToken);
        if (authorized)
        {
            capabilityLocked.TrySetResult();
            await releaseCommand.Task.WaitAsync(cancellationToken);
        }

        return authorized;
    }
}
