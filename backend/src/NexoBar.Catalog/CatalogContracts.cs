using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexoBar.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateProductRequest(
    string OperationalName,
    string Price,
    [property: Required] bool? RequiresPreparation);

internal sealed record ProductResponse(
    Guid Id,
    string OperationalName,
    string Price,
    bool IsActive,
    bool IsAvailable,
    bool RequiresPreparation);
