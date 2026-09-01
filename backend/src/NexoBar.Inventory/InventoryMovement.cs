namespace NexoBar.Inventory;

internal sealed class InventoryMovement
{
    internal const string ReconciliationNature = "Reconciliation";

    private InventoryMovement() { }

    internal InventoryMovement(
        Guid id,
        Guid inventoryItemId,
        long movementRevision,
        decimal quantity,
        decimal? previousRegisteredQuantity,
        decimal resultingRegisteredQuantity,
        Guid countObservationId,
        DateTimeOffset occurredAt,
        Guid actorIdentityId)
    {
        Id = id;
        InventoryItemId = inventoryItemId;
        MovementRevision = movementRevision;
        Nature = ReconciliationNature;
        Quantity = quantity;
        PreviousRegisteredQuantity = previousRegisteredQuantity;
        ResultingRegisteredQuantity = resultingRegisteredQuantity;
        CountObservationId = countObservationId;
        OccurredAt = occurredAt;
        ActorIdentityId = actorIdentityId;
    }

    internal Guid Id { get; private set; }
    internal Guid InventoryItemId { get; private set; }
    internal long MovementRevision { get; private set; }
    internal string Nature { get; private set; } = string.Empty;
    internal decimal Quantity { get; private set; }
    internal decimal? PreviousRegisteredQuantity { get; private set; }
    internal decimal ResultingRegisteredQuantity { get; private set; }
    internal Guid? CountObservationId { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
}
