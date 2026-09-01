using System.Text;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class DeliveryProgressMigrationTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Migration_materializes_narrow_history_and_command_tables()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);

        Assert.Equal(
            new[] { "delivery_commands", "delivery_history" },
            await ReadTableNamesAsync(connection, token));
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    [Fact]
    public async Task All_PostgreSQL_identifiers_remain_within_63_bytes()
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
                WHERE table_schema = 'order_operations'
                UNION ALL
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = 'order_operations'
                UNION ALL
                SELECT constraint_name
                FROM information_schema.table_constraints
                WHERE table_schema = 'order_operations'
                UNION ALL
                SELECT indexname
                FROM pg_indexes
                WHERE schemaname = 'order_operations'
            ) AS identifiers
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var identifier = reader.GetString(0);
            Assert.InRange(Encoding.UTF8.GetByteCount(identifier), 1, 63);
        }
    }

    private static async Task<string[]> ReadTableNamesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'order_operations'
              AND table_name IN ('delivery_history', 'delivery_commands')
            ORDER BY table_name
            """;
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }
}
