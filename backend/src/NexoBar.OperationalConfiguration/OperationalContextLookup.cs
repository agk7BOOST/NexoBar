using Microsoft.EntityFrameworkCore;

namespace NexoBar.OperationalConfiguration;

internal sealed class OperationalContextLookup(
    OperationalConfigurationDbContext dbContext) : IOrderContextConfiguration
{
    public async Task<ConfiguredOrderContext?> ResolveConfiguredContextAsync(
        Guid contextId,
        CancellationToken cancellationToken) =>
        await dbContext.Contexts.AsNoTracking()
            .Where(context => context.Id == contextId)
            .Select(context => new ConfiguredOrderContext(context.Id, context.OperationalName))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<OperationalContextReference>> ListConfiguredContextsAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Contexts.AsNoTracking()
            .OrderBy(context => context.OperationalName)
            .ThenBy(context => context.Id)
            .Select(context => new OperationalContextReference(
                context.Id,
                context.OperationalName))
            .ToArrayAsync(cancellationToken);
}
