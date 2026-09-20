using System.Data.Common;

namespace NexoBar.OperationalConfiguration;

/// <summary>
/// Narrow collaboration through which OperationalConfiguration obtains the
/// current authenticated actor and stabilizes GeneralConfiguration authority.
/// The implementation belongs to IdentitiesAndCapabilities.
/// </summary>
public interface IOperationalConfigurationAuthorization
{
    Task<OperationalConfigurationAuthenticatedActor?> StabilizeSessionAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken);

    Task<bool> StabilizeGeneralConfigurationAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken);
}

public sealed record OperationalConfigurationAuthenticatedActor(Guid IdentityId);
