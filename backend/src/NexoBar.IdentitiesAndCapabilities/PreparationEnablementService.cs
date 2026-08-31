using Microsoft.EntityFrameworkCore;
using NexoBar.OperationalConfiguration;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class PreparationEnablementService(
    IdentitiesAndCapabilitiesDbContext dbContext,
    IPreparationResponsibilityLookup preparationResponsibilities)
{
    internal async Task<GrantPreparationEnablementOutcome> GrantAsync(
        Guid identityId,
        Guid preparationResponsibilityId,
        CancellationToken cancellationToken)
    {
        if (!await preparationResponsibilities.ExistsAsync(
                preparationResponsibilityId,
                cancellationToken))
        {
            return GrantPreparationEnablementOutcome.PreparationResponsibilityNotFound;
        }

        if (!await dbContext.Identities.AnyAsync(
                identity => identity.Id == identityId,
                cancellationToken))
        {
            return GrantPreparationEnablementOutcome.IdentityNotFound;
        }

        if (await dbContext.PreparationEnablements.AnyAsync(
                enablement =>
                    enablement.IdentityId == identityId &&
                    enablement.PreparationResponsibilityId ==
                        preparationResponsibilityId,
                cancellationToken))
        {
            return GrantPreparationEnablementOutcome.AlreadyGranted;
        }

        dbContext.PreparationEnablements.Add(
            new PreparationEnablement(identityId, preparationResponsibilityId));
        await dbContext.SaveChangesAsync(cancellationToken);

        return GrantPreparationEnablementOutcome.Granted;
    }

    internal async Task<bool> RevokeAsync(
        Guid identityId,
        Guid preparationResponsibilityId,
        CancellationToken cancellationToken) =>
        await dbContext.PreparationEnablements
            .Where(enablement =>
                enablement.IdentityId == identityId &&
                enablement.PreparationResponsibilityId == preparationResponsibilityId)
            .ExecuteDeleteAsync(cancellationToken) == 1;
}

internal enum GrantPreparationEnablementOutcome
{
    Granted,
    AlreadyGranted,
    IdentityNotFound,
    PreparationResponsibilityNotFound
}
