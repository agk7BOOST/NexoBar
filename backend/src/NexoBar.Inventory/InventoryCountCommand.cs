namespace NexoBar.Inventory;

internal sealed class InventoryCountCommand
{
    internal const string RecordCountCommandKind = "RecordInventoryCount";

    private InventoryCountCommand() { }

    internal InventoryCountCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid inventoryItemId,
        decimal observedQuantity,
        CountObservationResponse result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = RecordCountCommandKind;
        InventoryItemId = inventoryItemId;
        ObservedQuantity = observedQuantity;
        ResultCountObservationId = result.CountObservationId;
        ResultObservedMovementRevision = result.ObservedMovementRevision;
        ResultObservedOperationalUnit = result.ObservedOperationalUnit;
        ResultObservedAt = result.ObservedAt;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = string.Empty;
    internal Guid InventoryItemId { get; private set; }
    internal decimal ObservedQuantity { get; private set; }
    internal Guid ResultCountObservationId { get; private set; }
    internal long ResultObservedMovementRevision { get; private set; }
    internal string ResultObservedOperationalUnit { get; private set; } = string.Empty;
    internal DateTimeOffset ResultObservedAt { get; private set; }

    internal bool Matches(Guid actorIdentityId, Guid inventoryItemId, decimal quantity) =>
        ActorIdentityId == actorIdentityId &&
        string.Equals(CommandKind, RecordCountCommandKind, StringComparison.Ordinal) &&
        InventoryItemId == inventoryItemId &&
        ObservedQuantity == quantity;

    internal CountObservationResponse ToResponse() => new(
        ResultCountObservationId,
        InventoryItemId,
        InventoryQuantity.Format(ObservedQuantity),
        ResultObservedMovementRevision,
        ResultObservedOperationalUnit,
        ResultObservedAt);
}
