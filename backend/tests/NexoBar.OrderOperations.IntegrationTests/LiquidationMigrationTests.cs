using System.Text;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class LiquidationMigrationTests(OrderOperationsApiFixture fixture)
{
    private const string PreviousMigration =
        "20260904152524_AddAuthoritativePendingComposition";
    private const string CurrentMigration = "20260905055531_AddLiquidation";

    [Fact]
    public async Task Migration_is_incremental_reversible_and_model_is_current()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);
        try
        {
            Assert.False(await TableExistsAsync("liquidations", token));
            Assert.False(await TableExistsAsync("liquidation_history", token));
            Assert.False(await TableExistsAsync("liquidation_commands", token));

            await fixture.MigrateOrderOperationsAsync(CurrentMigration, token);
            Assert.True(await TableExistsAsync("liquidations", token));
            Assert.True(await TableExistsAsync("liquidation_history", token));
            Assert.True(await TableExistsAsync("liquidation_commands", token));
            Assert.False(await fixture.HasPendingModelChangesAsync());

            var identifiers = await ReadIntroducedIdentifiersAsync(token);
            Assert.Contains("UX_liquidations_order", identifiers);
            Assert.Contains("FK_liquidation_history_liquidations", identifiers);
            Assert.Contains("UX_liquidation_commands_liquidation", identifiers);
            Assert.All(identifiers, identifier => Assert.InRange(
                Encoding.UTF8.GetByteCount(identifier), 1, 63));

            await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);
            Assert.False(await TableExistsAsync("liquidations", token));
            Assert.False(await TableExistsAsync("liquidation_history", token));
            Assert.False(await TableExistsAsync("liquidation_commands", token));
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync(CurrentMigration, token);
            await fixture.ResetAsync(token);
        }
    }

    private async Task<bool> TableExistsAsync(
        string table,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT to_regclass('order_operations.' || @table) IS NOT NULL";
        command.Parameters.AddWithValue("table", table);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<string[]> ReadIntroducedIdentifiersAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT conname
            FROM pg_constraint
            WHERE connamespace = 'order_operations'::regnamespace
              AND conrelid IN (
                  'order_operations.liquidations'::regclass,
                  'order_operations.liquidation_history'::regclass,
                  'order_operations.liquidation_commands'::regclass)
            UNION ALL
            SELECT indexname
            FROM pg_indexes
            WHERE schemaname = 'order_operations'
              AND tablename IN (
                  'liquidations',
                  'liquidation_history',
                  'liquidation_commands')
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var identifiers = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            identifiers.Add(reader.GetString(0));
        }

        return identifiers.Distinct(StringComparer.Ordinal).ToArray();
    }
}
