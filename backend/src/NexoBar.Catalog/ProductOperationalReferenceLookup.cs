using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NexoBar.Catalog;

public interface IProductOperationalReferenceLookup
{
    Task<IReadOnlyList<ProductOperationalReference>> ReadByIdsAsync(
        IReadOnlyCollection<Guid> productIds,
        DbTransaction transaction,
        CancellationToken cancellationToken);
}

public sealed record ProductOperationalReference(
    Guid ProductId,
    string OperationalName);

internal sealed class ProductOperationalReferenceLookup(CatalogDbContext dbContext) :
    IProductOperationalReferenceLookup
{
    public async Task<IReadOnlyList<ProductOperationalReference>> ReadByIdsAsync(
        IReadOnlyCollection<Guid> productIds,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var connection = transaction.Connection
            ?? throw new InvalidOperationException(
                "The OrderOperations transaction must have an active connection.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "The OrderOperations transaction connection must be open.");
        }

        dbContext.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await dbContext.Database.UseTransactionAsync(transaction, cancellationToken);
        if (!ReferenceEquals(
                dbContext.Database.CurrentTransaction?.GetDbTransaction(),
                transaction))
        {
            throw new InvalidOperationException(
                "Catalog must use the OrderOperations PostgreSQL transaction.");
        }

        var ids = productIds.Distinct().ToArray();
        return await dbContext.Products.AsNoTracking()
            .Where(product => ids.Contains(product.Id))
            .Select(product => new ProductOperationalReference(
                product.Id,
                product.OperationalName))
            .ToArrayAsync(cancellationToken);
    }
}
