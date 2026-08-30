using System.Text.Json.Serialization;

namespace NexoBar.OperationalConfiguration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreatePreparationResponsibilityRequest(string OperationalName);

public sealed record PreparationResponsibilityResponse(
    Guid Id,
    string OperationalName);
