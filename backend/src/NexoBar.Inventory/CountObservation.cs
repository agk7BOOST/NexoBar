namespace NexoBar.Inventory;

internal sealed class CountObservation
{
    private CountObservation() { }

    internal CountObservation(
        Guid id,
        Guid inventoryItemId,
        decimal observedQuantity,
        long observedMovementRevision,
        string observedOperationalUnit,
        DateTimeOffset observedAt,
        Guid actorIdentityId)
    {
        Id = id;
        InventoryItemId = inventoryItemId;
        ObservedQuantity = observedQuantity;
        ObservedMovementRevision = observedMovementRevision;
        ObservedOperationalUnit = observedOperationalUnit;
        ObservedAt = observedAt;
        ActorIdentityId = actorIdentityId;
    }

    internal Guid Id { get; private set; }
    internal Guid InventoryItemId { get; private set; }
    internal decimal ObservedQuantity { get; private set; }
    internal long ObservedMovementRevision { get; private set; }
    internal string ObservedOperationalUnit { get; private set; } = string.Empty;
    internal DateTimeOffset ObservedAt { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
}
