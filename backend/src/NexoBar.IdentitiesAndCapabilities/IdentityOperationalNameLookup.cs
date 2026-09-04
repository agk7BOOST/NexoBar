using Microsoft.EntityFrameworkCore;

namespace NexoBar.IdentitiesAndCapabilities;

public interface IIdentityOperationalNameLookup
{
    Task<IReadOnlyList<IdentityOperationalName>> ReadByIdsAsync(
        IReadOnlyCollection<Guid> identityIds,
        CancellationToken cancellationToken);
}

public sealed record IdentityOperationalName(
    Guid IdentityId,
    string OperationalName);

internal sealed class IdentityOperationalNameLookup(
    IdentitiesAndCapabilitiesDbContext dbContext) : IIdentityOperationalNameLookup
{
    public async Task<IReadOnlyList<IdentityOperationalName>> ReadByIdsAsync(
        IReadOnlyCollection<Guid> identityIds,
        CancellationToken cancellationToken)
    {
        var ids = identityIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return await dbContext.Identities.AsNoTracking()
            .Where(identity => ids.Contains(identity.Id))
            .Select(identity => new IdentityOperationalName(
                identity.Id,
                identity.OperationalName))
            .ToArrayAsync(cancellationToken);
    }
}
