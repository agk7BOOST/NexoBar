using System.Data.Common;
using System.Text.Json.Serialization;

namespace NexoBar.OperationalConfiguration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RenameConfigurationRequest(
    string ExpectedCurrentOperationalName, bool ExpectedIsActive, string NewOperationalName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigurationLifecycleRequest(
    string ExpectedCurrentOperationalName, bool ExpectedIsActive);

internal sealed record ConfigurationLifecycleResponse(
    Guid Id, string OperationalName, bool IsActive, bool IsDeleted);

public interface IContextOperationalParticipation
{
    Task<bool> HasParticipationAsync(Guid contextId, DbTransaction transaction, CancellationToken cancellationToken);
}

public interface IDestinationProductReferences
{
    Task<bool> HasProductReferencesAsync(Guid destinationId, bool activeOnly, DbTransaction transaction, CancellationToken cancellationToken);
}

public interface IDestinationOperationalParticipation
{
    Task<bool> HasParticipationAsync(Guid destinationId, DbTransaction transaction, CancellationToken cancellationToken);
}

public interface IDestinationEnablementReferences
{
    Task<bool> HasEnablementsAsync(Guid destinationId, DbTransaction transaction, CancellationToken cancellationToken);
}

internal sealed record ConfigurationMutationResult(
    int Status, string? Code = null, string? Detail = null, ConfigurationLifecycleResponse? Result = null);
