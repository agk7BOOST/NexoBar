using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiquidateSimpleRequest(string? DeclaredPaymentMedium);

internal sealed record LiquidationResponse(
    Guid LiquidationId,
    Guid OrderId,
    string Mode,
    string FunctionalAmount,
    string? DeclaredPaymentMedium,
    DateTimeOffset OccurredAt,
    bool IsFrozen);

internal static class LiquidationModes
{
    internal const string Simple = "Simple";
    internal const string ExternalCollection = "ExternalCollection";
}

internal static class LiquidationEligibilityBlockers
{
    internal const string AlreadyLiquidated = "already_liquidated";
    internal const string PendingComposition = "pending_composition";
    internal const string UnresolvedFulfillment = "unresolved_fulfillment";
    internal const string StateInconsistent = "state_inconsistent";
}
