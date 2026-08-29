using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FirstConfirmationRequest(
    [property: Required] string? Context,
    [property: Required] IReadOnlyList<FirstConfirmationItemRequest>? Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FirstConfirmationItemRequest(
    [property: Required, JsonRequired] Guid ProductId,
    [property: Required, JsonRequired] int Quantity);

internal sealed record FirstConfirmationResponse(
    Guid OperationalReference,
    string Context,
    FirstIncorporationResponse FirstIncorporation);

internal sealed record FirstIncorporationResponse(
    Guid Id,
    DateTimeOffset ConfirmedAt,
    IReadOnlyList<ConfirmedItemResponse> Items);

internal sealed record ConfirmedItemResponse(
    Guid ProductId,
    int Quantity,
    string AppliedPrice);
