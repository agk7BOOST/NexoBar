using System.Text;
using Npgsql;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryMigrationTests(InventoryApiFixture fixture)
{
    private const string InitialMigration = "20260901073703_InitialInventory";
    private const string CountReconciliationMigration =
        "20260901090459_AddInventoryCountReconciliation";
    private const string EverydayMovementsMigration =
        "20260904023608_AddEverydayInventoryMovements";

    [Fact]
    public async Task Initial_migration_has_safe_up_and_down()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        try
        {
            await fixture.MigrateInventoryAsync("0", token);
            Assert.False(await TableExistsAsync("inventory_items", token));
            Assert.False(await TableExistsAsync("item_creation_commands", token));

            await fixture.MigrateInventoryAsync(InitialMigration, token);
            Assert.True(await TableExistsAsync("inventory_items", token));
            Assert.True(await TableExistsAsync("item_creation_commands", token));
        }
        finally
        {
            await fixture.MigrateInventoryAsync(EverydayMovementsMigration, token);
        }
    }

    [Fact]
    public async Task Count_reconciliation_migration_is_incremental_and_reversible()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        try
        {
            await fixture.MigrateInventoryAsync(InitialMigration, token);
            Assert.False(await TableExistsAsync("count_observations", token));
            Assert.False(await TableExistsAsync("inventory_movements", token));
            Assert.False(await TableExistsAsync("count_commands", token));
            Assert.False(await TableExistsAsync("movement_commands", token));
            Assert.True(await TableExistsAsync("inventory_items", token));

            await fixture.MigrateInventoryAsync(CountReconciliationMigration, token);
            Assert.True(await TableExistsAsync("count_observations", token));
            Assert.True(await TableExistsAsync("inventory_movements", token));
            Assert.True(await TableExistsAsync("count_commands", token));
            Assert.True(await TableExistsAsync("movement_commands", token));
        }
        finally
        {
            await fixture.MigrateInventoryAsync(EverydayMovementsMigration, token);
        }
    }

    [Fact]
    public async Task Everyday_movements_migration_is_incremental_and_reversible()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        try
        {
            await fixture.MigrateInventoryAsync(CountReconciliationMigration, token);
            await using var beforeConnection = new NpgsqlConnection(
                fixture.ConnectionString);
            await beforeConnection.OpenAsync(token);
            Assert.DoesNotContain(
                "intent_quantity",
                await ReadColumnNamesAsync(
                    beforeConnection,
                    "movement_commands",
                    token));

            await fixture.MigrateInventoryAsync(EverydayMovementsMigration, token);
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync(token);
            var intent = await ReadColumnAsync(
                connection,
                "movement_commands",
                "intent_quantity",
                token);
            Assert.Equal("numeric", intent.DataType);
            Assert.Equal(28, intent.NumericPrecision);
            Assert.Equal(12, intent.NumericScale);
            Assert.Equal("YES", intent.IsNullable);
            Assert.Equal(
                "YES",
                (await ReadColumnAsync(
                    connection,
                    "movement_commands",
                    "count_observation_id",
                    token)).IsNullable);
        }
        finally
        {
            await fixture.MigrateInventoryAsync(EverydayMovementsMigration, token);
        }
    }

    [Fact]
    public async Task Physical_schema_has_exact_quantity_revision_name_and_command_storage()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);

        var quantity = await ReadColumnAsync(
            connection,
            "inventory_items",
            "current_registered_quantity",
            token);
        Assert.Equal("numeric", quantity.DataType);
        Assert.Equal(28, quantity.NumericPrecision);
        Assert.Equal(12, quantity.NumericScale);
        Assert.Equal("YES", quantity.IsNullable);

        var revision = await ReadColumnAsync(
            connection,
            "inventory_items",
            "movement_revision",
            token);
        Assert.Equal("bigint", revision.DataType);
        Assert.Equal("NO", revision.IsNullable);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'inventory'
              AND indexname = 'UX_inventory_items_normalized_name'
            """;
        var indexDefinition = Assert.IsType<string>(
            await command.ExecuteScalarAsync(token));
        Assert.Contains("UNIQUE", indexDefinition, StringComparison.Ordinal);

        var commandColumns = await ReadColumnNamesAsync(
            connection,
            "item_creation_commands",
            token);
        Assert.Contains("actor_identity_id", commandColumns);
        Assert.Contains("command_kind", commandColumns);
        Assert.Contains("intent_normalized_operational_name", commandColumns);
        Assert.Contains("intent_operational_unit", commandColumns);
        Assert.Contains("result_item_id", commandColumns);
        Assert.DoesNotContain("session_id", commandColumns);

        foreach (var (table, column) in new[]
                 {
                     ("count_observations", "observed_quantity"),
                     ("inventory_movements", "quantity"),
                     ("inventory_movements", "previous_registered_quantity"),
                     ("inventory_movements", "resulting_registered_quantity"),
                     ("count_commands", "observed_quantity"),
                     ("movement_commands", "result_observed_quantity"),
                     ("movement_commands", "intent_quantity"),
                     ("movement_commands", "result_previous_registered_quantity"),
                     ("movement_commands", "result_resulting_registered_quantity")
                 })
        {
            var metadata = await ReadColumnAsync(connection, table, column, token);
            Assert.Equal("numeric", metadata.DataType);
            Assert.Equal(28, metadata.NumericPrecision);
            Assert.Equal(12, metadata.NumericScale);
        }

        var movementCommandColumns = await ReadColumnNamesAsync(
            connection,
            "movement_commands",
            token);
        Assert.DoesNotContain("result_difference", movementCommandColumns);
        Assert.DoesNotContain("session_id", movementCommandColumns);
        Assert.DoesNotContain("product_id", movementCommandColumns);

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText =
            """
            SELECT COUNT(*)
            FROM information_schema.table_constraints
            WHERE constraint_schema = 'inventory'
              AND constraint_type = 'FOREIGN KEY'
              AND constraint_name ILIKE '%actor%'
            """;
        Assert.Equal(0L, await foreignKeys.ExecuteScalarAsync(token));
    }

    [Fact]
    public async Task Quantity_null_does_not_constrain_revision_to_zero()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);

        await fixture.SetRegisteredStateAsync(item.Id, null, 4, token);

        var persisted = await fixture.ReadItemAsync(item.Id, token);
        Assert.Null(persisted.CurrentRegisteredQuantity);
        Assert.Equal(4, persisted.MovementRevision);
    }

    [Fact]
    public async Task Model_has_no_pending_changes()
    {
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    [Fact]
    public async Task All_inventory_identifiers_are_within_PostgreSQL_limit()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT identifier
            FROM (
                SELECT table_name AS identifier
                FROM information_schema.tables
                WHERE table_schema = 'inventory'
                UNION ALL
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = 'inventory'
                UNION ALL
                SELECT constraint_name
                FROM information_schema.table_constraints
                WHERE table_schema = 'inventory'
                UNION ALL
                SELECT indexname
                FROM pg_indexes
                WHERE schemaname = 'inventory'
            ) AS identifiers
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(reader.GetString(0)), 1, 63);
        }
    }

    private async Task<bool> TableExistsAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'inventory'
                  AND table_name = @table_name)
            """;
        command.Parameters.AddWithValue("table_name", tableName);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<ColumnMetadata> ReadColumnAsync(
        NpgsqlConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT data_type, numeric_precision, numeric_scale, is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'inventory'
              AND table_name = @table_name
              AND column_name = @column_name
            """;
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("column_name", columnName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new ColumnMetadata(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.GetString(3));
    }

    private static async Task<string[]> ReadColumnNamesAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'inventory'
              AND table_name = @table_name
            ORDER BY ordinal_position
            """;
        command.Parameters.AddWithValue("table_name", tableName);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private sealed record ColumnMetadata(
        string DataType,
        int? NumericPrecision,
        int? NumericScale,
        string IsNullable);
}
