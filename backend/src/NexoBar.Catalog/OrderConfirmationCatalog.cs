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

public interface IOrderAppliedPriceCatalog
{
    Task<ConfiguredCatalogProductPrice?> ReadConfiguredPriceAsync(
        Guid productId,
        DbTransaction transaction,
        CancellationToken cancellationToken);
}

public sealed record ConfiguredCatalogProductPrice(
    Guid ProductId,
    decimal Price);

public sealed record OrderConfirmationCatalogProduct(
    Guid ProductId,
    string OperationalName,
    decimal Price,
    bool IsActive,
    bool IsAvailable,
    bool RequiresPreparation,
    Guid? PreparationResponsibilityId,
    bool IsPreparationDestinationActive = true);

internal sealed class OrderConfirmationCatalog(CatalogDbContext dbContext,
    NexoBar.OperationalConfiguration.IPreparationResponsibilityLookup preparationResponsibilities) :
    IOrderConfirmationCatalog, IOrderAppliedPriceCatalog
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
                       group_id,
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

        var activeDestinations = new HashSet<Guid>();
        foreach (var destination in products.Where(x => x.IsActive && x.RequiresPreparation)
            .Select(x => x.PreparationResponsibilityId!.Value).Distinct().Order())
            if (await preparationResponsibilities.IsActiveAsync(destination, transaction, cancellationToken))
                activeDestinations.Add(destination);

        return products
            .Select(product => new OrderConfirmationCatalogProduct(
                product.Id,
                product.OperationalName,
                product.Price,
                product.IsActive,
                product.IsAvailable,
                product.RequiresPreparation,
                product.PreparationResponsibilityId,
                !product.RequiresPreparation || activeDestinations.Contains(product.PreparationResponsibilityId!.Value)))
            .ToArray();
    }

    public async Task<ConfiguredCatalogProductPrice?> ReadConfiguredPriceAsync(
        Guid productId,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var connection = transaction.Connection
            ?? throw new InvalidOperationException(
                "The OrderOperations transaction must have an active connection.");

        dbContext.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await dbContext.Database.UseTransactionAsync(transaction, cancellationToken);

        var product = await dbContext.Products
            .FromSqlInterpolated(
                $"""
                SELECT id, group_id, operational_name, normalized_operational_name, price,
                       is_active, is_available, requires_preparation,
                       preparation_responsibility_id
                FROM catalog.products
                WHERE id = {productId}
                FOR SHARE
                """)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        return product is null
            ? null
            : new ConfiguredCatalogProductPrice(product.Id, product.Price);
    }
}
