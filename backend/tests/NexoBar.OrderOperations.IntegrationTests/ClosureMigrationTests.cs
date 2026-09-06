using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ClosureMigrationTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Migration_preserves_liquidated_orders_without_automatically_closing_and_is_reversible()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token);
        var liquidation = Assert.Single(await fixture.ReadLiquidationsAsync(token));
        await fixture.MigrateOrderOperationsAsync("20260905055531_AddLiquidation", token);
        try
        {
            await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT to_regclass('order_operations.closures') IS NULL";
            Assert.True(Assert.IsType<bool>(await command.ExecuteScalarAsync(token)));
            await fixture.MigrateOrderOperationsAsync("20260906120000_AddContentQuantityState", token);
            await ClosureTestSupport.AssertCountsAsync(fixture, 0, token);
            Assert.Equal(liquidation.Id, Assert.Single(await fixture.ReadLiquidationsAsync(token)).Id);
            Assert.Single(await fixture.ReadLiquidationHistoryAsync(token));
            var read = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, order.OperationalReference, token);
            Assert.True(read.IsClosureEligible);
            Assert.False(read.IsClosed);
            Assert.False(await fixture.HasPendingModelChangesAsync());
            await fixture.MigrateOrderOperationsAsync("20260905055531_AddLiquidation", token);
            Assert.True(Assert.IsType<bool>(await command.ExecuteScalarAsync(token)));
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync("20260906120000_AddContentQuantityState", token);
            await fixture.ResetAsync(token);
        }
    }

    [Fact]
    public async Task Database_enforces_one_closure_per_order()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token);
        using var response = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, Guid.NewGuid(), token);
        await ClosureTestSupport.ReadSuccessAsync(response, token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        db.Closures.Add(new Closure(Guid.CreateVersion7(), Guid.Parse(order.OperationalReference), DateTimeOffset.UtcNow, fixture.DefaultOrderOperationsActor.IdentityId));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
        await ClosureTestSupport.AssertCountsAsync(fixture, 1, token);
    }
}
