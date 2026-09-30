using Microsoft.EntityFrameworkCore;

namespace NexoBar.OperationalConfiguration;

internal sealed class OperationalContextLookup(
    OperationalConfigurationDbContext dbContext) : IOrderContextConfiguration
{
    public async Task<ConfiguredOrderContext?> ResolveConfiguredContextAsync(
        Guid contextId,
        CancellationToken cancellationToken) =>
        await dbContext.Contexts.AsNoTracking()
            .Where(context => context.Id == contextId && context.IsActive)
            .Select(context => new ConfiguredOrderContext(context.Id, context.OperationalName))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<OperationalContextReference>> ListConfiguredContextsAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Contexts.AsNoTracking().Where(context => context.IsActive)
            .OrderBy(context => context.OperationalName)
            .ThenBy(context => context.Id)
            .Select(context => new OperationalContextReference(
                context.Id,
                context.OperationalName))
            .ToArrayAsync(cancellationToken);

    public async Task<ConfiguredOrderContext?> ResolveConfiguredContextAsync(
        Guid contextId, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken)
    {
        dbContext.Database.SetDbConnection(transaction.Connection!, contextOwnsConnection: false);
        await dbContext.Database.UseTransactionAsync(transaction, cancellationToken);
        var context = await dbContext.Contexts.FromSqlInterpolated(
                $"SELECT * FROM operational_configuration.contexts WHERE id = {contextId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return context is { IsActive: true } ? new(context.Id, context.OperationalName) : null;
    }
}
