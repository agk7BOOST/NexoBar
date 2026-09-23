using System.Data.Common;

namespace NexoBar.IdentitiesAndCapabilities;

/// <summary>Module-owned evidence that an Identity must remain attributable.</summary>
public interface IOrderFunctionalIdentityAttribution
{
    Task<bool> HasFunctionalAttributionAsync(
        Guid identityId, DbTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>Inventory History, including Counts consumed by Reconciliation.</summary>
public interface IInventoryFunctionalIdentityAttribution
{
    Task<bool> HasFunctionalAttributionAsync(
        Guid identityId, DbTransaction transaction, CancellationToken cancellationToken);

    Task InvalidateUnusedCountsAsync(
        Guid identityId, DbTransaction transaction, CancellationToken cancellationToken);
}
