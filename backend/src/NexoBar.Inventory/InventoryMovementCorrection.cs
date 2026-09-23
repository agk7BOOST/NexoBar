namespace NexoBar.Inventory;

internal sealed class InventoryMovementCorrection
{
    private InventoryMovementCorrection() { }

    internal InventoryMovementCorrection(
        Guid idempotencyKey, Guid actorIdentityId, Guid rootMovementId,
        Guid inventoryItemId, long sequence, long movementRevision,
        string previousNature, decimal previousQuantity, string correctedNature,
        decimal correctedQuantity, decimal deltaApplied,
        decimal? resultingRegisteredQuantity, DateTimeOffset occurredAtUtc)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        RootMovementId = rootMovementId;
        InventoryItemId = inventoryItemId;
        Sequence = sequence;
        MovementRevision = movementRevision;
        PreviousNature = previousNature;
        PreviousQuantity = previousQuantity;
        CorrectedNature = correctedNature;
        CorrectedQuantity = correctedQuantity;
        DeltaApplied = deltaApplied;
        ResultingRegisteredQuantity = resultingRegisteredQuantity;
        OccurredAtUtc = occurredAtUtc;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal Guid RootMovementId { get; private set; }
    internal Guid InventoryItemId { get; private set; }
    internal long Sequence { get; private set; }
    internal long MovementRevision { get; private set; }
    internal string PreviousNature { get; private set; } = "";
    internal decimal PreviousQuantity { get; private set; }
    internal string CorrectedNature { get; private set; } = "";
    internal decimal CorrectedQuantity { get; private set; }
    internal decimal DeltaApplied { get; private set; }
    internal decimal? ResultingRegisteredQuantity { get; private set; }
    internal DateTimeOffset OccurredAtUtc { get; private set; }
}
