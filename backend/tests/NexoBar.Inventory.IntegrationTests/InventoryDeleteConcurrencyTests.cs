using System.Data;
using System.Net;
using Npgsql;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryDeleteConcurrencyTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task Delete_lock_first_wins_and_later_movement_observes_not_found()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Delete lock first", "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, 1, 0, token);
        var actor = await fixture.CreateActorAsync(
            "delete-lock-first",
            token,
            FunctionalResponsibility.InventoryConfiguration,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, token);
        var csrf = await fixture.GetAntiforgeryTokenAsync(token);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            token);
        await LockItemAsync(connection, transaction, item.Id, token);
        var deleteTask = fixture.PostDeleteAsync(item.Id, Guid.NewGuid(), token, csrf);
        try
        {
            Assert.True(await fixture.WaitForDatabaseLockAsync(
                "FROM inventory.inventory_items",
                TimeSpan.FromSeconds(10),
                token));
            var movementTask = fixture.PostEntryAsync(
                item.Id,
                Guid.NewGuid(),
                "1",
                token,
                csrf);
            Assert.True(await fixture.WaitForDatabaseLockAsync(
                "FROM inventory.inventory_items",
                TimeSpan.FromSeconds(10),
                token));

            await transaction.CommitAsync(token);
            using var delete = await deleteTask;
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
            using var movement = await movementTask;
            await InventoryTestAssertions.AssertProblemAsync(
                movement,
                HttpStatusCode.NotFound,
                "inventory.item.not_found",
                token);
            Assert.Empty(await fixture.ReadMovementsAsync(token));
        }
        finally
        {
            await ObserveAsync(deleteTask);
        }
    }

    [Fact]
    public async Task First_movement_commit_wins_and_delete_sees_history_conflict()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Movement lock first", "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, 1, 0, token);
        var actor = await fixture.CreateActorAsync(
            "movement-lock-first",
            token,
            FunctionalResponsibility.InventoryConfiguration,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, token);
        var csrf = await fixture.GetAntiforgeryTokenAsync(token);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            token);
        await LockItemAsync(connection, transaction, item.Id, token);
        var movementTask = fixture.PostEntryAsync(
            item.Id,
            Guid.NewGuid(),
            "1",
            token,
            csrf);
        try
        {
            Assert.True(await fixture.WaitForDatabaseLockAsync(
                "FROM inventory.inventory_items",
                TimeSpan.FromSeconds(10),
                token));
            await transaction.CommitAsync(token);
            using var movement = await movementTask;
            movement.EnsureSuccessStatusCode();

            using var delete = await fixture.PostDeleteAsync(
                item.Id,
                Guid.NewGuid(),
                token,
                csrf);
            await InventoryTestAssertions.AssertProblemAsync(
                delete,
                HttpStatusCode.Conflict,
                "inventory.item.delete_movement_history_conflict",
                token);
            Assert.Equal(item.Id, (await fixture.ReadItemAsync(item.Id, token)).Id);
            Assert.Single(await fixture.ReadMovementsAsync(token));
        }
        finally
        {
            await ObserveAsync(movementTask);
        }
    }

    private static async Task LockItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id FROM inventory.inventory_items WHERE id = @id FOR UPDATE";
        command.Parameters.AddWithValue("id", itemId);
        Assert.Equal(itemId, await command.ExecuteScalarAsync(cancellationToken));
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
