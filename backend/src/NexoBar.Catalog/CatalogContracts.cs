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
    Guid? PreparationResponsibilityId,
    Guid? GroupId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateGroupRequest(string OperationalName);

internal sealed record GroupResponse(Guid Id, string OperationalName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ChangeProductGroupRequest(
    [property: JsonRequired] Guid? ExpectedCurrentGroupId,
    [property: JsonRequired] Guid? NewGroupId);

internal sealed record ProductGroupResponse(Guid ProductId, Guid? GroupId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ChangeProductOperationalNameRequest(
    string ExpectedCurrentOperationalName,
    string NewOperationalName);

internal sealed record ProductOperationalNameResponse(Guid ProductId, string OperationalName);

internal sealed record ProductLifecycleResponse(Guid ProductId, bool IsActive, bool IsAvailable);
internal sealed record ProductDeleteResponse(Guid ProductId);

internal sealed record AvailabilityAdministrationProductResponse(
    Guid Id,
    string OperationalName,
    bool IsAvailable);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ChangeProductAvailabilityRequest(
    [property: JsonRequired] bool? ExpectedCurrentAvailability,
    [property: JsonRequired] bool? NewAvailability);

internal sealed record ProductAvailabilityResponse(Guid ProductId, bool IsAvailable);

public sealed record OperationalProductResponse(
    Guid Id,
    string OperationalName,
    string Price,
    bool IsAvailable);

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
