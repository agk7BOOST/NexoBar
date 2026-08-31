using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PreparationRequirementSnapshotMigrationTests(
    OrderOperationsApiFixture fixture)
{
    private const string PreviousMigration = "20260831171256_AddDeliveryState";
    private const string CurrentMigration =
        "20260831202815_CapturePreparationRequirementAtConfirmation";

    [Fact]
    public async Task Migration_backfills_by_exact_content_identity_and_has_no_default()
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
            await AssertColumnDefinitionAsync(token);

            await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);
            Assert.False(await ColumnExistsAsync(token));

            await fixture.MigrateOrderOperationsAsync(CurrentMigration, token);
            await AssertBackfillAsync(incorporationId, token);
            await AssertColumnDefinitionAsync(token);
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync(CurrentMigration, token);
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
            VALUES (@order_id, 'Mesa snapshot migration');

            INSERT INTO order_operations.incorporations (id, order_id, ordinal)
            VALUES (@incorporation_id, @order_id, 1);

            INSERT INTO order_operations.incorporation_contents
                (incorporation_id, content_ordinal, product_id,
                 quantity, applied_price, instruction)
            VALUES
                (@incorporation_id, 1, @same_product, 2, 3, NULL),
                (@incorporation_id, 2, @same_product, 3, 3, NULL);

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
        command.Parameters.AddWithValue("same_product", Guid.CreateVersion7());
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
            SELECT content_ordinal, requires_preparation_at_confirmation
            FROM order_operations.incorporation_contents
            WHERE incorporation_id = @incorporation_id
            ORDER BY content_ordinal
            """;
        command.Parameters.AddWithValue("incorporation_id", incorporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var values = new List<(int ContentOrdinal, bool RequiresPreparation)>();
        while (await reader.ReadAsync(token))
        {
            values.Add((reader.GetInt32(0), reader.GetBoolean(1)));
        }

        Assert.Equal([(1, false), (2, true)], values);
    }

    private async Task AssertColumnDefinitionAsync(CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'order_operations'
              AND table_name = 'incorporation_contents'
              AND column_name = 'requires_preparation_at_confirmation'
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal("boolean", reader.GetString(0));
        Assert.Equal("NO", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.False(await reader.ReadAsync(token));
    }

    private async Task<bool> ColumnExistsAsync(CancellationToken token)
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
                  AND table_name = 'incorporation_contents'
                  AND column_name = 'requires_preparation_at_confirmation')
            """;
        return Assert.IsType<bool>(await command.ExecuteScalarAsync(token));
    }
}
