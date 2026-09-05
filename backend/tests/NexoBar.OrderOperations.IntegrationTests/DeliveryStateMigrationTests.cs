using System.Text;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class DeliveryStateMigrationTests(OrderOperationsApiFixture fixture)
{
    private const string PreviousMigration = "20260831063910_AddPreparationStart";
    private const string CurrentMigration = "20260831171256_AddDeliveryState";
    private const string LatestMigration =
        "20260905101316_AddClosure";

    [Fact]
    public async Task Migration_backfills_every_existing_content_and_has_safe_down()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);

        try
        {
            var incorporationId = Guid.CreateVersion7();
            await SeedPreviousSchemaAsync(incorporationId, token);

            await fixture.MigrateOrderOperationsAsync(CurrentMigration, token);
            await AssertBackfillAsync(incorporationId, token);
            await AssertPhysicalStructureAsync(token);

            await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);
            Assert.False(await DeliveryStateTableExistsAsync(token));

            await fixture.MigrateOrderOperationsAsync(CurrentMigration, token);
            await AssertBackfillAsync(incorporationId, token);
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync(LatestMigration, token);
            await fixture.ResetAsync(token);
        }
    }

    private async Task SeedPreviousSchemaAsync(
        Guid incorporationId,
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO order_operations.orders (id, context)
            VALUES (@order_id, 'Mesa migration');

            INSERT INTO order_operations.incorporations (id, order_id, ordinal)
            VALUES (@incorporation_id, @order_id, 1);

            INSERT INTO order_operations.incorporation_contents
                (incorporation_id, content_ordinal, product_id,
                 quantity, applied_price, instruction)
            VALUES
                (@incorporation_id, 1, @direct_product, 2, 3, NULL),
                (@incorporation_id, 2, @prepared_product, 3, 5, 'sin sal');

            INSERT INTO order_operations.preparation_work
                (id, incorporation_id, content_ordinal,
                 preparation_responsibility_id, total_quantity,
                 pending_quantity, in_preparation_quantity, ready_quantity)
            VALUES
                (@work_id, @incorporation_id, 2,
                 @responsibility_id, 3, 3, 0, 0);
            """;
        command.Parameters.AddWithValue("order_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("incorporation_id", incorporationId);
        command.Parameters.AddWithValue("direct_product", Guid.CreateVersion7());
        command.Parameters.AddWithValue("prepared_product", Guid.CreateVersion7());
        command.Parameters.AddWithValue("work_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("responsibility_id", Guid.CreateVersion7());
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task AssertBackfillAsync(
        Guid incorporationId,
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT content_ordinal, delivered_quantity
            FROM order_operations.delivery_states
            WHERE incorporation_id = @incorporation_id
            ORDER BY content_ordinal
            """;
        command.Parameters.AddWithValue("incorporation_id", incorporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var states = new List<(int ContentOrdinal, int DeliveredQuantity)>();
        while (await reader.ReadAsync(token))
        {
            states.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        Assert.Equal([(1, 0), (2, 0)], states);
    }

    private async Task AssertPhysicalStructureAsync(CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT conname, pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE conrelid = 'order_operations.delivery_states'::regclass
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token))
        {
            definitions.Add(reader.GetString(0), reader.GetString(1));
        }

        Assert.Contains(
            "PRIMARY KEY (incorporation_id, content_ordinal)",
            definitions["PK_order_operations_delivery_states"]);
        Assert.Contains(
            "delivered_quantity >= 0",
            definitions["CK_order_operations_delivery_states_delivered_non_negative"]);
        Assert.Contains(
            "FOREIGN KEY (incorporation_id, content_ordinal)",
            definitions["FK_order_operations_delivery_states_content"]);
        Assert.All(
            definitions.Keys,
            identifier => Assert.InRange(Encoding.UTF8.GetByteCount(identifier), 1, 63));
    }

    private async Task<bool> DeliveryStateTableExistsAsync(CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT to_regclass('order_operations.delivery_states') IS NOT NULL
            """;
        return Assert.IsType<bool>(await command.ExecuteScalarAsync(token));
    }
}
