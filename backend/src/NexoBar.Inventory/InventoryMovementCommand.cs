namespace NexoBar.Inventory;

internal sealed class InventoryMovementCommand
{
    internal const string ReconcileCountCommandKind = "ReconcileInventoryCount";

    private InventoryMovementCommand() { }

    internal InventoryMovementCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid inventoryItemId,
        Guid countObservationId,
        ReconcileInventoryCountResponse result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = ReconcileCountCommandKind;
        InventoryItemId = inventoryItemId;
        CountObservationId = countObservationId;
        ResultOutcome = result.Outcome;
        ResultMovementId = result.MovementId;
        ResultOccurredAt = result.OccurredAt;
        ResultPreviousRegisteredQuantity = Parse(result.PreviousRegisteredQuantity);
        ResultObservedQuantity = ParseRequired(result.ObservedQuantity);
        ResultResultingRegisteredQuantity = ParseRequired(
            result.ResultingRegisteredQuantity);
        ResultMovementRevision = result.MovementRevision;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = string.Empty;
    internal Guid InventoryItemId { get; private set; }
    internal Guid CountObservationId { get; private set; }
    internal string ResultOutcome { get; private set; } = string.Empty;
    internal Guid? ResultMovementId { get; private set; }
    internal DateTimeOffset? ResultOccurredAt { get; private set; }
    internal decimal? ResultPreviousRegisteredQuantity { get; private set; }
    internal decimal ResultObservedQuantity { get; private set; }
    internal decimal ResultResultingRegisteredQuantity { get; private set; }
    internal long ResultMovementRevision { get; private set; }

    internal bool Matches(
        Guid actorIdentityId,
        Guid inventoryItemId,
        Guid countObservationId) =>
        ActorIdentityId == actorIdentityId &&
        string.Equals(CommandKind, ReconcileCountCommandKind, StringComparison.Ordinal) &&
        InventoryItemId == inventoryItemId &&
        CountObservationId == countObservationId;

    internal ReconcileInventoryCountResponse ToResponse() => new(
        InventoryItemId,
        CountObservationId,
        ResultOutcome,
        ResultMovementId,
        ResultOccurredAt,
        InventoryQuantity.Format(ResultPreviousRegisteredQuantity),
        InventoryQuantity.Format(ResultObservedQuantity),
        InventoryQuantity.Format(
            ResultPreviousRegisteredQuantity is null
                ? null
                : ResultObservedQuantity - ResultPreviousRegisteredQuantity.Value),
        InventoryQuantity.Format(ResultResultingRegisteredQuantity),
        ResultMovementRevision);

    private static decimal? Parse(string? value) =>
        value is null ? null : ParseRequired(value);

    private static decimal ParseRequired(string value) =>
        decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
