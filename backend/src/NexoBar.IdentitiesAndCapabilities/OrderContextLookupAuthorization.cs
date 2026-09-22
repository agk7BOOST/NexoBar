using System.Data.Common;
using NexoBar.OperationalConfiguration;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class OrderContextLookupAuthorization(
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOrderOperationsCapabilityStabilizer capabilityStabilizer) :
    IOrderContextLookupAuthorization
{
    public async Task<OrderContextLookupAuthorizationOutcome> AuthorizeAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var session = await sessionStabilizer.StabilizeAsync(
            transaction, cancellationToken);
        if (session is null)
        {
            return OrderContextLookupAuthorizationOutcome.Unauthenticated;
        }

        return await capabilityStabilizer.StabilizeResponsibilityAsync(
                session.IdentityId, transaction, cancellationToken)
            ? OrderContextLookupAuthorizationOutcome.Authorized
            : OrderContextLookupAuthorizationOutcome.Forbidden;
    }
}
