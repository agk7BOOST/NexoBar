namespace NexoBar.Inventory;

internal sealed class InventoryMovement
{
    internal const string ReconciliationNature = "Reconciliation";
    internal const string EntryNature = "Entry";
    internal const string ManualExitNature = "ManualExit";
    internal const string WasteNature = "Waste";

    private InventoryMovement() { }

    private InventoryMovement(
        Guid id,
        Guid inventoryItemId,
        long movementRevision,
        string nature,
        decimal quantity,
        decimal? previousRegisteredQuantity,
        decimal resultingRegisteredQuantity,
        Guid? countObservationId,
        DateTimeOffset occurredAt,
        Guid actorIdentityId)
    {
        Id = id;
        InventoryItemId = inventoryItemId;
        MovementRevision = movementRevision;
        Nature = nature;
        Quantity = quantity;
        PreviousRegisteredQuantity = previousRegisteredQuantity;
        ResultingRegisteredQuantity = resultingRegisteredQuantity;
        CountObservationId = countObservationId;
        OccurredAt = occurredAt;
        ActorIdentityId = actorIdentityId;
    }

    internal static InventoryMovement Reconciliation(
        Guid id,
        Guid inventoryItemId,
        long movementRevision,
        decimal quantity,
        decimal? previousRegisteredQuantity,
        decimal resultingRegisteredQuantity,
        Guid countObservationId,
        DateTimeOffset occurredAt,
        Guid actorIdentityId) =>
        new(
            id,
            inventoryItemId,
            movementRevision,
            ReconciliationNature,
            quantity,
            previousRegisteredQuantity,
            resultingRegisteredQuantity,
            countObservationId,
            occurredAt,
            actorIdentityId);

    internal static InventoryMovement Entry(
        Guid id,
        Guid inventoryItemId,
        long movementRevision,
        decimal quantity,
        decimal previousRegisteredQuantity,
        decimal resultingRegisteredQuantity,
        DateTimeOffset occurredAt,
        Guid actorIdentityId) =>
        QuantityMovement(
            id,
            inventoryItemId,
            movementRevision,
            EntryNature,
            quantity,
            previousRegisteredQuantity,
            resultingRegisteredQuantity,
            occurredAt,
            actorIdentityId);

    internal static InventoryMovement ManualExit(
        Guid id,
        Guid inventoryItemId,
        long movementRevision,
        decimal quantity,
        decimal previousRegisteredQuantity,
        decimal resultingRegisteredQuantity,
        DateTimeOffset occurredAt,
        Guid actorIdentityId) =>
        QuantityMovement(
            id,
            inventoryItemId,
            movementRevision,
            ManualExitNature,
            quantity,
            previousRegisteredQuantity,
            resultingRegisteredQuantity,
            occurredAt,
            actorIdentityId);

    internal static InventoryMovement Waste(
        Guid id,
        Guid inventoryItemId,
        long movementRevision,
        decimal quantity,
        decimal previousRegisteredQuantity,
        decimal resultingRegisteredQuantity,
        DateTimeOffset occurredAt,
        Guid actorIdentityId) =>
        QuantityMovement(
            id,
            inventoryItemId,
            movementRevision,
            WasteNature,
            quantity,
            previousRegisteredQuantity,
            resultingRegisteredQuantity,
            occurredAt,
            actorIdentityId);

    private static InventoryMovement QuantityMovement(
        Guid id,
        Guid inventoryItemId,
        long movementRevision,
        string nature,
        decimal quantity,
        decimal previousRegisteredQuantity,
        decimal resultingRegisteredQuantity,
        DateTimeOffset occurredAt,
        Guid actorIdentityId) =>
        new(
            id,
            inventoryItemId,
            movementRevision,
            nature,
            quantity,
            previousRegisteredQuantity,
            resultingRegisteredQuantity,
            null,
            occurredAt,
            actorIdentityId);

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
