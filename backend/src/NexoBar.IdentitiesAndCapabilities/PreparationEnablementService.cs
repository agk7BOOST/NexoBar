using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await preparationResponsibilities.ExistsAsync(
                preparationResponsibilityId,
                transaction.GetDbTransaction(),
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

        await transaction.CommitAsync(cancellationToken);
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
