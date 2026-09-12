using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NexoBar.IdentitiesAndCapabilities;

/// <summary>Checks the current request's fixed destination snapshot without recording activity.</summary>
public interface IPreparationSubscriptionAuthorization
{
    Task<PreparationAuthorizationOutcome> AuthorizeAsync(
        IReadOnlyCollection<Guid> destinationIds,
        CancellationToken cancellationToken);
}

internal sealed class PreparationSubscriptionAuthorization(
    IdentitiesAndCapabilitiesDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IPreparationCapabilityStabilizer capabilities) : IPreparationSubscriptionAuthorization
{
    public async Task<PreparationAuthorizationOutcome> AuthorizeAsync(
        IReadOnlyCollection<Guid> destinationIds,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var dbTransaction = transaction.GetDbTransaction();
        var session = await sessionStabilizer.StabilizeAsync(dbTransaction, cancellationToken);
        if (session is null)
        {
            return PreparationAuthorizationOutcome.Unauthenticated;
        }

        if (destinationIds.Count == 0 ||
            !await capabilities.StabilizePreparationResponsibilityAsync(
                session.IdentityId, dbTransaction, cancellationToken))
        {
            return PreparationAuthorizationOutcome.Forbidden;
        }

        foreach (var destinationId in destinationIds.Distinct().Order())
        {
            if (!await capabilities.StabilizeExactEnablementAsync(
                    session.IdentityId, destinationId, dbTransaction, cancellationToken))
            {
                return PreparationAuthorizationOutcome.Forbidden;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return PreparationAuthorizationOutcome.Authorized;
    }
}
