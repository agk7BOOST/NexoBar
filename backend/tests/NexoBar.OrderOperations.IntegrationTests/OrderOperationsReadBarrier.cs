using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NexoBar.OrderOperations.IntegrationTests;

internal sealed class OrderOperationsReadBarrier : DbCommandInterceptor
{
    private readonly TaskCompletionSource reached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int intercepted;

    internal Task Reached => reached.Task;

    internal void Release() => release.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (IsOrderOperationsRead(command) && Interlocked.Exchange(ref intercepted, 1) == 0)
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }

        return result;
    }

    private static bool IsOrderOperationsRead(DbCommand command) =>
        command.CommandText.Contains("order_operations.incorporation_contents", StringComparison.OrdinalIgnoreCase) ||
        command.CommandText.Contains("order_operations.preparation_work", StringComparison.OrdinalIgnoreCase);
}
