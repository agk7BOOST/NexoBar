using System.Data.Common;

namespace NexoBar.OperationalConfiguration;

public interface IOrderContextLookupAuthorization
{
    Task<OrderContextLookupAuthorizationOutcome> AuthorizeAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken);
}

public enum OrderContextLookupAuthorizationOutcome
{
    Authorized,
    Unauthenticated,
    Forbidden
}
