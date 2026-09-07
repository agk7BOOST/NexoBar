using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ContentQuantityStateMigrationTests(OrderOperationsApiFixture fixture)
{
    private const string PreviousMigration = "20260906000853_AddDeliveryCorrection";
    private const string CurrentMigration = "20260906120000_AddContentQuantityState";

    [Fact]
    public async Task Migration_backfills_every_legacy_content_and_down_protects_nonzero_state()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);
        try
        {
            var incorporationId = Guid.CreateVersion7();
            await SeedContentsAsync(incorporationId, token);
            await fixture.MigrateOrderOperationsAsync(CurrentMigration, token);

            // Read only columns present at this historical migration.
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            var removedQuantities = await db.ContentQuantityStates.AsNoTracking()
                .Select(state => state.RemovedByCorrectionQuantity).ToArrayAsync(token);
            Assert.Equal(2, removedQuantities.Length);
            Assert.All(removedQuantities, removed => Assert.Equal(0, removed));
            Assert.All(new[]
            {
                "content_quantity_states",
                "PK_content_quantity_states",
                "CK_content_quantity_states_removed_non_negative",
                "FK_content_quantity_states_content"
            }, identifier => Assert.InRange(Encoding.UTF8.GetByteCount(identifier), 1, 63));

            await SetRemovedAsync(incorporationId, 1, token);
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                fixture.MigrateOrderOperationsAsync(PreviousMigration, token));
            Assert.Contains("Cannot remove ContentQuantityState", exception.MessageText);
            Assert.Equal(2, await fixture.CountContentQuantityStatesAsync(token));

            await SetRemovedAsync(incorporationId, 0, token);
            await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync(CurrentMigration, token);
            await fixture.ResetAsync(token);
        }
    }

    private async Task SeedContentsAsync(Guid incorporationId, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO order_operations.orders (id, context) VALUES (@order_id, 'legacy');
            INSERT INTO order_operations.incorporations (id, order_id, ordinal)
            VALUES (@incorporation_id, @order_id, 1);
            INSERT INTO order_operations.incorporation_contents
                (incorporation_id, content_ordinal, product_id, quantity,
                 requires_preparation_at_confirmation, applied_price, instruction)
            VALUES
                (@incorporation_id, 1, @product_one, 5, false, 10, NULL),
                (@incorporation_id, 2, @product_two, 2, false, 4, NULL);
            INSERT INTO order_operations.delivery_states
                (incorporation_id, content_ordinal, delivered_quantity)
            VALUES (@incorporation_id, 1, 2), (@incorporation_id, 2, 2);
            """;
        command.Parameters.AddWithValue("order_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("incorporation_id", incorporationId);
        command.Parameters.AddWithValue("product_one", Guid.CreateVersion7());
        command.Parameters.AddWithValue("product_two", Guid.CreateVersion7());
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task SetRemovedAsync(Guid incorporationId, int quantity, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE order_operations.content_quantity_states
            SET removed_by_correction_quantity = @quantity
            WHERE incorporation_id = @incorporation_id AND content_ordinal = 1
            """;
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("incorporation_id", incorporationId);
        await command.ExecuteNonQueryAsync(token);
    }
}
