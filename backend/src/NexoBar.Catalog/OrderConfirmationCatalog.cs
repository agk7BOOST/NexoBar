using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NexoBar.Catalog;

public interface IOrderConfirmationCatalog
{
    Task<IReadOnlyList<OrderConfirmationCatalogProduct>> ReadProductsAsync(
        IReadOnlyCollection<Guid> productIds,
        DbTransaction transaction,
        CancellationToken cancellationToken);
}

public sealed record OrderConfirmationCatalogProduct(
    Guid ProductId,
    decimal Price,
    bool IsActive,
    bool IsAvailable,
    bool RequiresPreparation,
    Guid? PreparationResponsibilityId);

internal sealed class OrderConfirmationCatalog(CatalogDbContext dbContext) :
    IOrderConfirmationCatalog
{
    public async Task<IReadOnlyList<OrderConfirmationCatalogProduct>> ReadProductsAsync(
        IReadOnlyCollection<Guid> productIds,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var connection = transaction.Connection
            ?? throw new InvalidOperationException(
                "The OrderOperations transaction must have an active connection.");

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
        var products = await dbContext.Products
            .FromSqlInterpolated(
                $"""
                SELECT id,
                       operational_name,
                       normalized_operational_name,
                       price,
                       is_active,
                       is_available,
                       requires_preparation,
                       preparation_responsibility_id
                FROM catalog.products
                WHERE id = ANY ({ids})
                FOR SHARE
                """)
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);

        return products
            .Select(product => new OrderConfirmationCatalogProduct(
                product.Id,
                product.Price,
                product.IsActive,
                product.IsAvailable,
                product.RequiresPreparation,
                product.PreparationResponsibilityId))
            .ToArray();
    }
}
