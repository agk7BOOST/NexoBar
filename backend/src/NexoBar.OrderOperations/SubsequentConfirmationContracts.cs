using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SubsequentConfirmationRequest(
    [property: Required, JsonRequired] Guid PendingCompositionId,
    [property: Required] IReadOnlyList<SubsequentConfirmationItemRequest>? Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SubsequentConfirmationItemRequest(
    [property: Required, JsonRequired] Guid ProductId,
    [property: Required, JsonRequired] int Quantity,
    string? Instruction = null);

internal sealed record SubsequentConfirmationResponse(
    string OperationalReference,
    SubsequentIncorporationResponse Incorporation);

internal sealed record SubsequentIncorporationResponse(
    Guid Id,
    int Ordinal,
    DateTimeOffset ConfirmedAt,
    IReadOnlyList<ConfirmedItemResponse> Items);
