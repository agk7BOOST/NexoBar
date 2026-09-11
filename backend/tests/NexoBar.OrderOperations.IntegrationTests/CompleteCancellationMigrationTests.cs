using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed partial class CompleteCancellationTests
{
    [Fact]
    public async Task Migration_no_backfill_reversible_when_empty_and_guards_terminal_history()
    {
        const string previous = "20260910120000_AllowZeroInterventionResults";
        const string current = "20260910222430_AddCompleteOrderCancellation";
        var target = await Setup();
        using var partial = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 7, Token);
        partial.EnsureSuccessStatusCode();
        var quantities = (await fixture.ReadContentQuantityStatesAsync(Token))
            .Select(x => (x.IncorporationId, x.ContentOrdinal, x.RemovedByCorrectionQuantity, x.CancelledQuantity)).ToArray();
        try
        {
            await fixture.MigrateOrderOperationsAsync(previous, Token);
            await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, Token);
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT to_regclass('order_operations.order_cancellation_states') IS NULL";
            Assert.True((bool)(await query.ExecuteScalarAsync(Token))!);
            await fixture.MigrateOrderOperationsAsync(current, Token);
            await Counts(0);
            Assert.Equal(quantities, (await fixture.ReadContentQuantityStatesAsync(Token))
                .Select(x => (x.IncorporationId, x.ContentOrdinal, x.RemovedByCorrectionQuantity, x.CancelledQuantity)).ToArray());
            Assert.True((await Read(target.OperationalReference)).IsEligible);
            using var cancelled = await Post(fixture.OrderOperationsClient, target.OperationalReference);
            var original = await Success(cancelled);
            var error = await Assert.ThrowsAsync<PostgresException>(() => fixture.MigrateOrderOperationsAsync(previous, Token));
            Assert.Contains("Cannot remove meaningful Complete Cancellation", error.MessageText);
            await Counts(1);
            Assert.Equal(original.CancellationId, (await Read(target.OperationalReference)).CancellationId);
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            Assert.Empty(await db.CompleteCancellationDetails.ToArrayAsync(Token));
            Assert.False(await fixture.HasPendingModelChangesAsync());
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync(current, Token);
            await fixture.ResetAsync(Token);
        }
    }

    [Fact]
    public async Task Migration_enforces_one_terminal_fact_exact_content_and_positive_details()
    {
        var target = await Setup();
        using var cancelled = await Post(fixture.OrderOperationsClient, target.OperationalReference);
        var result = await Success(cancelled);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var zero = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE order_operations.complete_cancellation_details SET direct_or_pending_quantity = 0 WHERE cancellation_id = {result.CancellationId}", Token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, zero.SqlState);
        var wrongContent = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE order_operations.complete_cancellation_details SET content_ordinal = 999 WHERE cancellation_id = {result.CancellationId}", Token));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, wrongContent.SqlState);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO order_operations.complete_cancellation_history SELECT {Guid.CreateVersion7()}, order_id, actor_identity_id, occurred_at, pending_composition_discarded, event_kind FROM order_operations.complete_cancellation_history WHERE id = {result.CancellationId}", Token));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        var wrongState = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE order_operations.order_cancellation_states SET cancellation_id = {Guid.NewGuid()} WHERE order_id = {result.OrderId}", Token));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, wrongState.SqlState);
        await Counts(1);
    }
}
