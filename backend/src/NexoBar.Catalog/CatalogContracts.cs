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
    bool RequiresPreparation,
    Guid? PreparationResponsibilityId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ChangeProductPriceRequest(
    string ExpectedCurrentPrice,
    string NewPrice);

internal sealed record ProductPriceResponse(
    Guid ProductId,
    string Price);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ChangeProductPreparationConfigurationRequest(
    [property: JsonRequired] Guid? ExpectedCurrentPreparationResponsibilityId,
    [property: JsonRequired] Guid? NewPreparationResponsibilityId);

internal sealed record ProductPreparationConfigurationResponse(
    Guid ProductId,
    bool RequiresPreparation,
    Guid? PreparationResponsibilityId);
