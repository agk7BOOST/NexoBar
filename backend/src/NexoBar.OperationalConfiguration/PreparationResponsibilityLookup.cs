using Microsoft.EntityFrameworkCore;

namespace NexoBar.OperationalConfiguration;

public interface IPreparationResponsibilityLookup
{
    Task<bool> ExistsAsync(Guid responsibilityId, CancellationToken cancellationToken);
}

internal sealed class PreparationResponsibilityLookup(
    OperationalConfigurationDbContext dbContext) : IPreparationResponsibilityLookup
{
    public Task<bool> ExistsAsync(
        Guid responsibilityId,
        CancellationToken cancellationToken) =>
        dbContext.PreparationResponsibilities
            .AsNoTracking()
            .AnyAsync(
                responsibility => responsibility.Id == responsibilityId,
                cancellationToken);
}
