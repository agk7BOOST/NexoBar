namespace NexoBar.OrderOperations;

internal static class OperationalInterventionState
{
    internal static bool IsCoherent(IncorporationContent content, PreparationWork? work,
        DeliveryState? delivery, ContentQuantityState? quantities)
    {
        if (content.Quantity <= 0 || quantities is null || delivery is null ||
            quantities.RemovedByCorrectionQuantity < 0 || quantities.CancelledQuantity < 0 ||
            content.RequiresPreparationAtConfirmation != (work is not null)) return false;
        var fulfillment = (long)content.Quantity - quantities.RemovedByCorrectionQuantity - quantities.CancelledQuantity;
        return fulfillment >= 0 && delivery.DeliveredQuantity >= 0 && delivery.DeliveredQuantity <= fulfillment &&
            (work is null || (work.TotalQuantity == fulfillment && work.PendingQuantity >= 0 &&
                work.InPreparationQuantity >= 0 && work.ReadyQuantity >= 0 &&
                (long)work.PendingQuantity + work.InPreparationQuantity + work.ReadyQuantity == fulfillment &&
                delivery.DeliveredQuantity <= work.ReadyQuantity));
    }
}
