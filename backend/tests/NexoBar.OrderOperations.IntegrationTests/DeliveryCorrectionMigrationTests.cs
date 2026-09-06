using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class DeliveryCorrectionMigrationTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Migration_preserves_delivery_state_and_original_history_and_is_reversible()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 3, token);
        var original = Assert.Single(await fixture.ReadDeliveryHistoryAsync(token));
        await fixture.MigrateOrderOperationsAsync("20260905101316_AddClosure", token);
        try
        {
            await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
            await using var sql = connection.CreateCommand();
            sql.CommandText = "SELECT to_regclass('order_operations.delivery_correction_history') IS NULL";
            Assert.True(Assert.IsType<bool>(await sql.ExecuteScalarAsync(token)));
            await fixture.MigrateOrderOperationsAsync("20260906000853_AddDeliveryCorrection", token);
            Assert.Equal(3, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
            Assert.Equal(original.Id, Assert.Single(await fixture.ReadDeliveryHistoryAsync(token)).Id);
            await DeliveryCorrectionTestSupport.CountsAsync(fixture, 0, token);
            Assert.False(await fixture.HasPendingModelChangesAsync());
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync("20260906000853_AddDeliveryCorrection", token);
        }
    }

    [Fact]
    public async Task Database_rejects_correction_that_would_increase_effective_delivery()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        db.DeliveryCorrectionHistory.Add(new DeliveryCorrectionHistory(new DeliveryCorrectionResponse(
            Guid.Parse(target.OperationalReference), target.IncorporationId, target.ContentOrdinal,
            Guid.CreateVersion7(), -1, 0, 1, DateTimeOffset.UtcNow), fixture.DefaultOrderOperationsActor.IdentityId));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }
}
