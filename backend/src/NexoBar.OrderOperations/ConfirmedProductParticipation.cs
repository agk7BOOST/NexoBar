using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.Catalog;

namespace NexoBar.OrderOperations;

internal sealed class ConfirmedProductParticipation(OrderOperationsDbContext dbContext)
    : IConfirmedProductParticipation
{
    public async Task<bool> HasConfirmedParticipationAsync(
        Guid productId, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The Catalog transaction must have an active connection.");
        dbContext.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await dbContext.Database.UseTransactionAsync(transaction, cancellationToken);
        if (!ReferenceEquals(dbContext.Database.CurrentTransaction?.GetDbTransaction(), transaction))
            throw new InvalidOperationException("OrderOperations must use the Catalog PostgreSQL transaction.");

        return await dbContext.IncorporationContents.AsNoTracking()
            .AnyAsync(content => content.ProductId == productId, cancellationToken);
    }
}
