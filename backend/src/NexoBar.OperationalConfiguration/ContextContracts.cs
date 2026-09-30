using System.Text.Json.Serialization;

namespace NexoBar.OperationalConfiguration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateOperationalContextRequest(string OperationalName);

public sealed record OperationalContextReference(Guid Id, string OperationalName, bool IsActive = true);

public sealed record ConfiguredOrderContext(Guid ContextId, string OperationalName);
public sealed record SelectableOperationalContextReference(Guid Id, string OperationalName);

public interface IOrderContextConfiguration
{
    Task<ConfiguredOrderContext?> ResolveConfiguredContextAsync(
        Guid contextId,
        CancellationToken cancellationToken);

    Task<ConfiguredOrderContext?> ResolveConfiguredContextAsync(
        Guid contextId,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OperationalContextReference>> ListConfiguredContextsAsync(
        CancellationToken cancellationToken);
}
