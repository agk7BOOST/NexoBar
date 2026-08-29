namespace NexoBar.OrderOperations;

internal sealed record OrderQueryResponse(
    string OperationalReference,
    string Context,
    IReadOnlyList<OrderIncorporationResponse> Incorporations);

internal sealed record OrderIncorporationResponse(
    Guid Id,
    int Ordinal,
    DateTimeOffset ConfirmedAt,
    IReadOnlyList<ConfirmedItemResponse> Items);
