using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NexoBar.OperationalConfiguration;

namespace NexoBar.Catalog;

internal sealed class DestinationProductReferences(CatalogDbContext db) : IDestinationProductReferences
{
    public async Task<bool> HasProductReferencesAsync(Guid id, bool activeOnly, DbTransaction transaction, CancellationToken token)
    {
        await AdoptAsync(transaction, token);
        return await db.Products.AsNoTracking().AnyAsync(x => x.PreparationResponsibilityId == id && (!activeOnly || x.IsActive), token);
    }
    private async Task AdoptAsync(DbTransaction transaction, CancellationToken token)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("The caller transaction must have an active connection.");
        db.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, token);
    }
}
