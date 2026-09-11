using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed partial class OperationalInterventionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Migration_accepts_balanced_zero_results_and_down_preserves_history(bool ready)
    {
        const string previous = "20260907054805_AddContentCancellation";
        const string current = "20260910120000_AllowZeroInterventionResults";
        var s = await Setup(3, ready ? 3 : 0, 0, 3); using var client = s.Client;
        var original = HistorySnapshot(await fixture.ReadPreparationHistoryAsync(Token));
        await fixture.MigrateOrderOperationsAsync(previous, Token);
        await fixture.MigrateOrderOperationsAsync(current, Token);
        Assert.Equal(original, HistorySnapshot(await fixture.ReadPreparationHistoryAsync(Token)));
        await fixture.MigrateOrderOperationsAsync("20260910222430_AddCompleteOrderCancellation", Token);
        using var intervention = await Intervene(client, s.Target, ready, 3);
        var result = await Success(intervention);
        var histories = HistorySnapshot(await fixture.ReadPreparationHistoryAsync(Token));
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => fixture.MigrateOrderOperationsAsync(previous, Token));
        Assert.Contains("Cannot restore positive Preparation results", rejected.MessageText);
        Assert.Equal(histories, HistorySnapshot(await fixture.ReadPreparationHistoryAsync(Token)));
        // The balanced and nonnegative checks remain active after relaxing only the total constraint.
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            var historyError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE order_operations.preparation_history SET resulting_pending_quantity = 1 WHERE id = {result.HistoryId}", Token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, historyError.SqlState);
            var commandError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE order_operations.preparation_commands SET result_total_quantity = -1 WHERE result_history_id = {result.HistoryId}", Token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, commandError.SqlState);
        }
        await fixture.ResetAsync(Token);
        try { await fixture.MigrateOrderOperationsAsync(previous, Token); }
        finally
        {
            await fixture.MigrateOrderOperationsAsync(current, Token);
            await fixture.MigrateOrderOperationsAsync("20260910222430_AddCompleteOrderCancellation", Token);
        }
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }
}
