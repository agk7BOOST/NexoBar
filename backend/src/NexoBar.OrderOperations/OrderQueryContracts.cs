namespace NexoBar.OrderOperations;

internal sealed record OrderQueryResponse(
    string OperationalReference,
    string Context,
    IReadOnlyList<OrderIncorporationResponse> Incorporations,
    string FunctionalAmount,
    bool IsLiquidationEligible,
    IReadOnlyList<string> LiquidationBlockers,
    bool IsLiquidated,
    bool IsFrozen,
    string? LiquidatedAmount,
    string? LiquidationMode,
    string? DeclaredPaymentMedium);

internal sealed record OrderIncorporationResponse(
    Guid Id,
    int Ordinal,
    DateTimeOffset ConfirmedAt,
    IReadOnlyList<ConfirmedItemResponse> Items);
