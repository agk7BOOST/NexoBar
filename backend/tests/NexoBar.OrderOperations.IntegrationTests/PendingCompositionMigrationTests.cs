using System.Text;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PendingCompositionMigrationTests(OrderOperationsApiFixture fixture)
{
    private const string PreviousMigration = "20260831214404_AddDeliveryProgress";
    private const string CurrentMigration =
        "20260904152524_AddAuthoritativePendingComposition";
    private const string LatestMigration = "20260906000853_AddDeliveryCorrection";

    [Fact]
    public async Task Migration_is_incremental_reversible_and_uses_safe_identifiers()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);
        try
        {
            Assert.False(await TableExistsAsync("pending_compositions", token));
            Assert.False(await TableExistsAsync("pending_composition_commands", token));
            Assert.False(await ColumnExistsAsync(
                "confirmation_history", "actor_identity_id", token));

            await fixture.MigrateOrderOperationsAsync(CurrentMigration, token);
            Assert.True(await TableExistsAsync("pending_compositions", token));
            Assert.True(await TableExistsAsync("pending_composition_commands", token));
            Assert.True(await ColumnExistsAsync(
                "confirmation_history", "actor_identity_id", token));
            Assert.True(await ColumnExistsAsync(
                "first_confirmation_commands", "actor_identity_id", token));
            Assert.True(await ColumnExistsAsync(
                "subsequent_confirmation_commands", "actor_identity_id", token));
            Assert.True(await ColumnExistsAsync(
                "subsequent_confirmation_commands",
                "intent_pending_composition_id",
                token));

            var identifiers = await ReadIntroducedIdentifiersAsync(token);
            Assert.Contains("UX_pending_compositions_order", identifiers);
            Assert.Contains("FK_pending_compositions_orders", identifiers);
            Assert.DoesNotContain(identifiers, identifier =>
                identifier.Contains("identit", StringComparison.OrdinalIgnoreCase) &&
                identifier.StartsWith("FK_", StringComparison.Ordinal));
            Assert.All(identifiers, identifier => Assert.InRange(
                Encoding.UTF8.GetByteCount(identifier), 1, 63));

            await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);
            Assert.False(await TableExistsAsync("pending_compositions", token));
            Assert.False(await TableExistsAsync("pending_composition_commands", token));
            Assert.False(await ColumnExistsAsync(
                "confirmation_history", "actor_identity_id", token));
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync(LatestMigration, token);
            await fixture.ResetAsync(token);
        }
    }

    private async Task<bool> TableExistsAsync(
        string table,
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT to_regclass('order_operations.' || @table) IS NOT NULL";
        command.Parameters.AddWithValue("table", table);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync(token));
    }

    private async Task<bool> ColumnExistsAsync(
        string table,
        string column,
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'order_operations'
                  AND table_name = @table
                  AND column_name = @column)
            """;
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync(token));
    }

    private async Task<string[]> ReadIntroducedIdentifiersAsync(
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT conname
            FROM pg_constraint
            WHERE connamespace = 'order_operations'::regnamespace
              AND conrelid IN (
                  'order_operations.pending_compositions'::regclass,
                  'order_operations.pending_composition_commands'::regclass)
            UNION ALL
            SELECT indexname
            FROM pg_indexes
            WHERE schemaname = 'order_operations'
              AND tablename IN (
                  'pending_compositions',
                  'pending_composition_commands')
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        var identifiers = new List<string>();
        while (await reader.ReadAsync(token))
        {
            identifiers.Add(reader.GetString(0));
        }

        return identifiers.Distinct(StringComparer.Ordinal).ToArray();
    }
}
