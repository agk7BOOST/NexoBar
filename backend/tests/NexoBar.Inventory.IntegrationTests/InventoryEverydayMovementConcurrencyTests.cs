using System.Data;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryEverydayMovementConcurrencyTests(
    InventoryApiFixture fixture)
{
    [Fact]
    public async Task Entry_and_manual_exit_on_same_item_serialize_without_lost_update()
    {
        await AssertConcurrentMovementsAsync(
            10,
            (item, key, csrf, token) => fixture.PostEntryAsync(
                item, key, "5", token, csrf),
            (item, key, csrf, token) => fixture.PostManualExitAsync(
                item, key, "3", token, csrf),
            12,
            token: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Multiple_exit_natures_can_both_cross_zero()
    {
        await AssertConcurrentMovementsAsync(
            2,
            (item, key, csrf, token) => fixture.PostManualExitAsync(
                item, key, "3", token, csrf),
            (item, key, csrf, token) => fixture.PostWasteAsync(
                item, key, "4", token, csrf),
            -5,
            token: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Same_kind_entries_serialize_into_coherent_revision_sequence()
    {
        await AssertConcurrentMovementsAsync(
            10,
            (item, key, csrf, token) => fixture.PostEntryAsync(
                item, key, "2", token, csrf),
            (item, key, csrf, token) => fixture.PostEntryAsync(
                item, key, "3", token, csrf),
            15,
            token: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Locked_item_does_not_block_movement_on_another_item()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var itemA = await AddEstablishedItemAsync("Concurrent A", 10, token);
        var itemB = await AddEstablishedItemAsync("Concurrent B", 20, token);
        var actor = await ActorAsync("different-items", token);
        await fixture.LoginAsync(actor, token);
        var csrf = await fixture.GetAntiforgeryTokenAsync(token);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            token);
        await LockItemAsync(connection, transaction, itemA.Id, token);
        var committed = false;
        var blockedTask = fixture.PostEntryAsync(
            itemA.Id, Guid.NewGuid(), "1", token, csrf);

        try
        {
            Assert.True(await fixture.WaitForDatabaseLockAsync(
                "FROM inventory.inventory_items",
                TimeSpan.FromSeconds(10),
                token));
            using var independent = await fixture.PostWasteAsync(
                itemB.Id, Guid.NewGuid(), "2", token, csrf)
                .WaitAsync(TimeSpan.FromSeconds(10), token);
            independent.EnsureSuccessStatusCode();
            Assert.False(blockedTask.IsCompleted);

            await transaction.CommitAsync(token);
            committed = true;
            using var blocked = await blockedTask;
            blocked.EnsureSuccessStatusCode();
        }
        finally
        {
            if (!committed)
            {
                await transaction.RollbackAsync(token);
            }

            await ObserveAsync(blockedTask);
        }

        Assert.Equal(11, (await fixture.ReadItemAsync(itemA.Id, token))
            .CurrentRegisteredQuantity);
        Assert.Equal(18, (await fixture.ReadItemAsync(itemB.Id, token))
            .CurrentRegisteredQuantity);
    }

    [Fact]
    public async Task Everyday_movement_invalidates_count_from_previous_revision()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await AddEstablishedItemAsync("Count invalidation", 10, token);
        var actor = await ActorAsync("count-invalidation", token);
        await fixture.LoginAsync(actor, token);
        using var countResponse = await fixture.PostCountAsync(
            item.Id, Guid.NewGuid(), "9", token);
        countResponse.EnsureSuccessStatusCode();
        var count = (await countResponse.Content
            .ReadFromJsonAsync<CountObservationResponse>(token))!;
        using var entry = await fixture.PostEntryAsync(
            item.Id, Guid.NewGuid(), "1", token);
        entry.EnsureSuccessStatusCode();

        using var reconciliation = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), count.CountObservationId, token);

        await InventoryTestAssertions.AssertProblemAsync(
            reconciliation,
            HttpStatusCode.Conflict,
            "inventory.reconciliation.count_invalidated",
            token);
        Assert.Equal(11, (await fixture.ReadItemAsync(item.Id, token))
            .CurrentRegisteredQuantity);
    }

    [Fact]
    public async Task Queued_movement_completes_before_later_count_and_count_uses_new_revision()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await AddEstablishedItemAsync("Count after movement", 10, token);
        var actor = await ActorAsync("count-after-movement", token);
        await fixture.LoginAsync(actor, token);
        var csrf = await fixture.GetAntiforgeryTokenAsync(token);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            token);
        await LockItemAsync(connection, transaction, item.Id, token);
        var committed = false;
        var movementTask = fixture.PostEntryAsync(
            item.Id, Guid.NewGuid(), "5", token, csrf);
        Task<HttpResponseMessage>? countTask = null;

        try
        {
            Assert.True(await fixture.WaitForDatabaseLockAsync(
                "FROM inventory.inventory_items",
                TimeSpan.FromSeconds(10),
                token));
            countTask = fixture.PostCountAsync(
                item.Id, Guid.NewGuid(), "15", token, csrf);
            Assert.True(await fixture.WaitForDatabaseLockAsync(
                "FROM inventory.inventory_items",
                TimeSpan.FromSeconds(10),
                token));

            await transaction.CommitAsync(token);
            committed = true;
            using var movementResponse = await movementTask;
            movementResponse.EnsureSuccessStatusCode();
            using var countResponse = await countTask;
            countResponse.EnsureSuccessStatusCode();
            var count = (await countResponse.Content
                .ReadFromJsonAsync<CountObservationResponse>(token))!;
            Assert.Equal(1, count.ObservedMovementRevision);
            using var reconciliation = await fixture.PostReconcileAsync(
                item.Id, Guid.NewGuid(), count.CountObservationId, token, csrf);
            reconciliation.EnsureSuccessStatusCode();
            var result = (await reconciliation.Content
                .ReadFromJsonAsync<ReconcileInventoryCountResponse>(token))!;
            Assert.Equal("no_discrepancy", result.Outcome);
            Assert.Equal(1, result.MovementRevision);
        }
        finally
        {
            if (!committed)
            {
                await transaction.RollbackAsync(token);
            }

            await ObserveAsync(movementTask);
            if (countTask is not null)
            {
                await ObserveAsync(countTask);
            }
        }
    }

    [Fact]
    public async Task Stabilized_inventory_operation_allows_movement_before_revocation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await AddEstablishedItemAsync("Authorization race", 10, token);
        var actor = await ActorAsync("authorization", token);
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
        var movementTask = InventoryApiFixture.PostMovementAsync(
            client, item.Id, Guid.NewGuid(), "entries", "1", token, csrf);
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
            releaseCommand.TrySetResult();
            using var movement = await movementTask;
            movement.EnsureSuccessStatusCode();
            await revokeTask;
            using var after = await InventoryApiFixture.PostMovementAsync(
                client, item.Id, Guid.NewGuid(), "entries", "1", token, csrf);
            Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        }
        finally
        {
            releaseCommand.TrySetResult();
            await ObserveAsync(movementTask);
            await ObserveAsync(revokeTask);
        }
    }

    private async Task AssertConcurrentMovementsAsync(
        decimal initialQuantity,
        Func<Guid, Guid, string, CancellationToken, Task<HttpResponseMessage>> first,
        Func<Guid, Guid, string, CancellationToken, Task<HttpResponseMessage>> second,
        decimal expectedQuantity,
        CancellationToken token)
    {
        await fixture.ResetAsync(token);
        var item = await AddEstablishedItemAsync(
            $"Same item {Guid.NewGuid():N}", initialQuantity, token);
        var actor = await ActorAsync("same-item", token);
        await fixture.LoginAsync(actor, token);
        var csrf = await fixture.GetAntiforgeryTokenAsync(token);

        var responses = await Task.WhenAll(
            first(item.Id, Guid.NewGuid(), csrf, token),
            second(item.Id, Guid.NewGuid(), csrf, token));
        try
        {
            Assert.All(responses, response => response.EnsureSuccessStatusCode());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        var persisted = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(expectedQuantity, persisted.CurrentRegisteredQuantity);
        Assert.Equal(2, persisted.MovementRevision);
        var movements = await fixture.ReadMovementsAsync(token);
        Assert.Equal(2, movements.Count);
        Assert.Equal([1L, 2L], movements.Select(value => value.MovementRevision));
        Assert.Equal(initialQuantity, movements[0].PreviousRegisteredQuantity);
        Assert.Equal(
            movements[0].ResultingRegisteredQuantity,
            movements[1].PreviousRegisteredQuantity);
        Assert.Equal(expectedQuantity, movements[1].ResultingRegisteredQuantity);
    }

    private async Task<InventoryItem> AddEstablishedItemAsync(
        string name,
        decimal quantity,
        CancellationToken token)
    {
        var item = await fixture.AddItemAsync(name, "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, quantity, 0, token);
        return item;
    }

    private Task<InventoryActor> ActorAsync(string suffix, CancellationToken token) =>
        fixture.CreateActorAsync(
            $"concurrency-movement-{suffix}-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);

    private static async Task LockItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id FROM inventory.inventory_items WHERE id = @id FOR UPDATE";
        command.Parameters.AddWithValue("id", itemId);
        Assert.Equal(itemId, await command.ExecuteScalarAsync(token));
    }

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
