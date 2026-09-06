namespace NexoBar.OrderOperations;

internal sealed record OrderDeliveryResponse(
    Guid OrderId,
    string OperationalReference,
    string CurrentContext,
    IReadOnlyList<OrderDeliveryContentResponse> Contents);

internal sealed record OrderDeliveryContentResponse(
    Guid IncorporationId,
    int IncorporationOrdinal,
    int ContentOrdinal,
    Guid ProductId,
    string ProductOperationalName,
    string? Instruction,
    int TotalQuantity,
    bool RequiresPreparationAtConfirmation,
    int? ReadyQuantity,
    int DeliveredQuantity,
    int DeliverableQuantity,
    int RemainingQuantity,
    int ConfirmedQuantity,
    int RemovedByCorrectionQuantity,
    int CurrentFulfillmentQuantity);

internal sealed record OrderDeliveryQueryResult(
    OrderDeliveryQueryOutcome Outcome,
    OrderDeliveryResponse? Response)
{
    internal static OrderDeliveryQueryResult Succeeded(
        OrderDeliveryResponse response) =>
        new(OrderDeliveryQueryOutcome.Succeeded, response);

    internal static OrderDeliveryQueryResult Unauthenticated() =>
        new(OrderDeliveryQueryOutcome.Unauthenticated, null);

    internal static OrderDeliveryQueryResult Forbidden() =>
        new(OrderDeliveryQueryOutcome.Forbidden, null);

    internal static OrderDeliveryQueryResult OrderNotFound() =>
        new(OrderDeliveryQueryOutcome.OrderNotFound, null);

    internal static OrderDeliveryQueryResult StateInconsistent() =>
        new(OrderDeliveryQueryOutcome.StateInconsistent, null);

    internal static OrderDeliveryQueryResult ProductReferenceInconsistent() =>
        new(OrderDeliveryQueryOutcome.ProductReferenceInconsistent, null);
}

internal enum OrderDeliveryQueryOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden,
    OrderNotFound,
    StateInconsistent,
    ProductReferenceInconsistent
}
