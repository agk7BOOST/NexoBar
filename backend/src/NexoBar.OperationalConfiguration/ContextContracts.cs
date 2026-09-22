using System.Text.Json.Serialization;

namespace NexoBar.OperationalConfiguration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateOperationalContextRequest(string OperationalName);

public sealed record OperationalContextReference(Guid Id, string OperationalName);

public sealed record ConfiguredOrderContext(Guid ContextId, string OperationalName);

public interface IOrderContextConfiguration
{
    Task<ConfiguredOrderContext?> ResolveConfiguredContextAsync(
        Guid contextId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OperationalContextReference>> ListConfiguredContextsAsync(
        CancellationToken cancellationToken);
}
