using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FirstConfirmationRequest(
    Guid? ContextId,
    string? Context,
    [property: Required] IReadOnlyList<FirstConfirmationItemRequest>? Items)
{
    // Kept for internal replay fixtures representing pre-ContextId payloads.
    internal FirstConfirmationRequest(
        string? legacyContext,
        IReadOnlyList<FirstConfirmationItemRequest>? items)
        : this(null, legacyContext, items)
    {
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FirstConfirmationItemRequest(
    [property: Required, JsonRequired] Guid ProductId,
    [property: Required, JsonRequired] int Quantity,
    string? Instruction = null,
    bool UnavailableProductExceptionRequested = false);

internal sealed record FirstConfirmationResponse(
    string OperationalReference,
    Guid ContextId,
    string Context,
    FirstIncorporationResponse FirstIncorporation);

internal sealed record FirstIncorporationResponse(
    Guid Id,
    DateTimeOffset ConfirmedAt,
    IReadOnlyList<ConfirmedItemResponse> Items);

internal sealed record ConfirmedItemResponse(
    Guid ProductId,
    string? ProductOperationalNameSnapshot,
    int Quantity,
    string AppliedPrice,
    string? Instruction,
    bool UnavailableProductExceptionApplied);
