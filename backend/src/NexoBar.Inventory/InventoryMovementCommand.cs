namespace NexoBar.Inventory;

internal sealed class InventoryMovementCommand
{
    internal const string ReconcileCountCommandKind = "ReconcileInventoryCount";
    internal const string RecordEntryCommandKind = "RecordInventoryEntry";
    internal const string RecordManualExitCommandKind = "RecordManualInventoryExit";
    internal const string RecordWasteCommandKind = "RecordInventoryWaste";

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
        IntentQuantity = null;
        ResultOutcome = result.Outcome;
        ResultMovementId = result.MovementId;
        ResultOccurredAt = result.OccurredAt;
        ResultPreviousRegisteredQuantity = Parse(result.PreviousRegisteredQuantity);
        ResultObservedQuantity = ParseRequired(result.ObservedQuantity);
        ResultResultingRegisteredQuantity = ParseRequired(
            result.ResultingRegisteredQuantity);
        ResultMovementRevision = result.MovementRevision;
    }

    internal InventoryMovementCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid inventoryItemId,
        string commandKind,
        decimal quantity,
        InventoryMovementResponse result)
    {
        if (!IsQuantityMovementCommand(commandKind))
        {
            throw new ArgumentOutOfRangeException(nameof(commandKind));
        }

        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = commandKind;
        InventoryItemId = inventoryItemId;
        CountObservationId = null;
        IntentQuantity = quantity;
        ResultOutcome = null;
        ResultMovementId = result.MovementId;
        ResultOccurredAt = result.OccurredAt;
        ResultPreviousRegisteredQuantity = ParseRequired(
            result.PreviousRegisteredQuantity);
        ResultObservedQuantity = null;
        ResultResultingRegisteredQuantity = ParseRequired(
            result.ResultingRegisteredQuantity);
        ResultMovementRevision = result.MovementRevision;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = string.Empty;
    internal Guid InventoryItemId { get; private set; }
    internal Guid? CountObservationId { get; private set; }
    internal decimal? IntentQuantity { get; private set; }
    internal string? ResultOutcome { get; private set; }
    internal Guid? ResultMovementId { get; private set; }
    internal DateTimeOffset? ResultOccurredAt { get; private set; }
    internal decimal? ResultPreviousRegisteredQuantity { get; private set; }
    internal decimal? ResultObservedQuantity { get; private set; }
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

    internal bool MatchesQuantityMovement(
        Guid actorIdentityId,
        Guid inventoryItemId,
        string commandKind,
        decimal quantity) =>
        ActorIdentityId == actorIdentityId &&
        string.Equals(CommandKind, commandKind, StringComparison.Ordinal) &&
        InventoryItemId == inventoryItemId &&
        IntentQuantity == quantity;

    internal ReconcileInventoryCountResponse ToResponse() => new(
        InventoryItemId,
        CountObservationId!.Value,
        ResultOutcome!,
        ResultMovementId,
        ResultOccurredAt,
        InventoryQuantity.Format(ResultPreviousRegisteredQuantity),
        InventoryQuantity.Format(ResultObservedQuantity!.Value),
        InventoryQuantity.Format(
            ResultPreviousRegisteredQuantity is null
                ? null
                : ResultObservedQuantity.Value - ResultPreviousRegisteredQuantity.Value),
        InventoryQuantity.Format(ResultResultingRegisteredQuantity),
        ResultMovementRevision);

    internal InventoryMovementResponse ToQuantityMovementResponse() => new(
        ResultMovementId!.Value,
        InventoryItemId,
        NatureForCommand(CommandKind),
        InventoryQuantity.Format(IntentQuantity!.Value),
        InventoryQuantity.Format(ResultPreviousRegisteredQuantity!.Value),
        InventoryQuantity.Format(ResultResultingRegisteredQuantity),
        ResultMovementRevision,
        ResultOccurredAt!.Value);

    internal static string NatureForCommand(string commandKind) => commandKind switch
    {
        RecordEntryCommandKind => InventoryMovement.EntryNature,
        RecordManualExitCommandKind => InventoryMovement.ManualExitNature,
        RecordWasteCommandKind => InventoryMovement.WasteNature,
        _ => throw new InvalidOperationException(
            $"Command kind '{commandKind}' is not an ordinary Inventory Movement.")
    };

    internal static bool IsQuantityMovementCommand(string commandKind) =>
        commandKind is RecordEntryCommandKind or
            RecordManualExitCommandKind or
            RecordWasteCommandKind;

    private static decimal? Parse(string? value) =>
        value is null ? null : ParseRequired(value);

    private static decimal ParseRequired(string value) =>
        decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
