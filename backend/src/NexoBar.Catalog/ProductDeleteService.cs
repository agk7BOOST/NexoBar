using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Catalog;

internal sealed class ProductDeleteService(
    CatalogDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    ICatalogConfigurationCapabilityStabilizer catalogConfiguration,
    IConfirmedProductParticipation participation)
{
    private const long DeleteNamespace = 0x44454C4554455052;

    internal async Task<ProductDeleteResult> DeleteAsync(Guid key, Guid productId, CancellationToken token)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
        Span<byte> bytes = stackalloc byte[16];
        key.TryWriteBytes(bytes, bigEndian: true, out _);
        var lockKey = DeleteNamespace ^ BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", token);

        var actor = (await sessionStabilizer.StabilizeAsync(transaction.GetDbTransaction(), token))?.IdentityId;
        if (actor is null) return ProductDeleteResult.AuthenticationRequired();

        var prior = await dbContext.ProductDeleteCommands.AsNoTracking()
            .SingleOrDefaultAsync(command => command.IdempotencyKey == key, token);
        if (prior is not null)
        {
            await transaction.CommitAsync(token);
            return prior.Matches(actor.Value, productId)
                ? ProductDeleteResult.Deleted() : ProductDeleteResult.IdempotencyConflict();
        }

        if (!await catalogConfiguration.StabilizeResponsibilityAsync(
                actor.Value, transaction.GetDbTransaction(), token))
            return ProductDeleteResult.Forbidden();

        var product = await dbContext.Products.FromSqlInterpolated($"""
            SELECT id, group_id, operational_name, normalized_operational_name, price,
                   is_active, is_available, requires_preparation, preparation_responsibility_id
            FROM catalog.products WHERE id = {productId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (product is null) return ProductDeleteResult.NotFound();

        if (await participation.HasConfirmedParticipationAsync(
                productId, transaction.GetDbTransaction(), token))
            return ProductDeleteResult.ConfirmedParticipation();

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM catalog.products WHERE id = {productId}", token);
        dbContext.ProductDeleteCommands.Add(new ProductDeleteCommand(key, actor.Value, productId));
        await dbContext.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return ProductDeleteResult.Deleted();
    }
}

internal enum ProductDeleteOutcome
{
    Deleted, NotFound, ConfirmedParticipation, IdempotencyConflict,
    AuthenticationRequired, Forbidden
}

internal sealed record ProductDeleteResult(ProductDeleteOutcome Outcome)
{
    internal static ProductDeleteResult Deleted() => new(ProductDeleteOutcome.Deleted);
    internal static ProductDeleteResult NotFound() => new(ProductDeleteOutcome.NotFound);
    internal static ProductDeleteResult ConfirmedParticipation() => new(ProductDeleteOutcome.ConfirmedParticipation);
    internal static ProductDeleteResult IdempotencyConflict() => new(ProductDeleteOutcome.IdempotencyConflict);
    internal static ProductDeleteResult AuthenticationRequired() => new(ProductDeleteOutcome.AuthenticationRequired);
    internal static ProductDeleteResult Forbidden() => new(ProductDeleteOutcome.Forbidden);
}
