namespace NexoBar.Inventory;

internal sealed class InventoryRetireCommand
{
    internal const string CommandKindValue = "RetireInventoryItem";

    private InventoryRetireCommand() { }

    internal InventoryRetireCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid inventoryItemId,
        bool expectedCurrentIsActive,
        InventoryLifecycleResponse result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        InventoryItemId = inventoryItemId;
        CommandKind = CommandKindValue;
        IntentExpectedCurrentIsActive = expectedCurrentIsActive;
        ResultOperationalName = result.OperationalName;
        ResultOperationalUnit = result.OperationalUnit;
        ResultIsActive = result.IsActive;
        ResultMovementRevision = result.MovementRevision;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal Guid InventoryItemId { get; private set; }
    internal string CommandKind { get; private set; } = string.Empty;
    internal bool IntentExpectedCurrentIsActive { get; private set; }
    internal string ResultOperationalName { get; private set; } = string.Empty;
    internal string ResultOperationalUnit { get; private set; } = string.Empty;
    internal bool ResultIsActive { get; private set; }
    internal long ResultMovementRevision { get; private set; }

    internal bool Matches(
        Guid actorIdentityId,
        Guid inventoryItemId,
        bool expectedCurrentIsActive) =>
        ActorIdentityId == actorIdentityId &&
        InventoryItemId == inventoryItemId &&
        string.Equals(CommandKind, CommandKindValue, StringComparison.Ordinal) &&
        IntentExpectedCurrentIsActive == expectedCurrentIsActive;

    internal InventoryLifecycleResponse ToResponse() =>
        new(
            InventoryItemId,
            ResultOperationalName,
            ResultOperationalUnit,
            ResultIsActive,
            false,
            false,
            ResultMovementRevision);
}

internal sealed class InventoryReactivateCommand
{
    internal const string CommandKindValue = "ReactivateInventoryItem";

    private InventoryReactivateCommand() { }

    internal InventoryReactivateCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid inventoryItemId,
        bool expectedCurrentIsActive,
        string? newOperationalName,
        InventoryLifecycleResponse result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        InventoryItemId = inventoryItemId;
        CommandKind = CommandKindValue;
        IntentExpectedCurrentIsActive = expectedCurrentIsActive;
        IntentNewOperationalName = newOperationalName;
        ResultOperationalName = result.OperationalName;
        ResultOperationalUnit = result.OperationalUnit;
        ResultIsActive = result.IsActive;
        ResultMovementRevision = result.MovementRevision;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal Guid InventoryItemId { get; private set; }
    internal string CommandKind { get; private set; } = string.Empty;
    internal bool IntentExpectedCurrentIsActive { get; private set; }
    internal string? IntentNewOperationalName { get; private set; }
    internal string ResultOperationalName { get; private set; } = string.Empty;
    internal string ResultOperationalUnit { get; private set; } = string.Empty;
    internal bool ResultIsActive { get; private set; }
    internal long ResultMovementRevision { get; private set; }

    internal bool Matches(
        Guid actorIdentityId,
        Guid inventoryItemId,
        bool expectedCurrentIsActive,
        string? newOperationalName) =>
        ActorIdentityId == actorIdentityId &&
        InventoryItemId == inventoryItemId &&
        string.Equals(CommandKind, CommandKindValue, StringComparison.Ordinal) &&
        IntentExpectedCurrentIsActive == expectedCurrentIsActive &&
        string.Equals(
            IntentNewOperationalName,
            newOperationalName,
            StringComparison.Ordinal);

    internal InventoryLifecycleResponse ToResponse() =>
        new(
            InventoryItemId,
            ResultOperationalName,
            ResultOperationalUnit,
            ResultIsActive,
            false,
            true,
            ResultMovementRevision);
}

internal sealed class InventoryUnitCorrectionCommand
{
    internal const string CommandKindValue = "CorrectInventoryUnit";

    private InventoryUnitCorrectionCommand() { }

    internal InventoryUnitCorrectionCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid inventoryItemId,
        string expectedCurrentUnit,
        string newUnit,
        InventoryUnitCorrectionResponse result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        InventoryItemId = inventoryItemId;
        CommandKind = CommandKindValue;
        IntentExpectedCurrentUnit = expectedCurrentUnit;
        IntentNewUnit = newUnit;
        ResultOperationalUnit = result.OperationalUnit;
        ResultOutcome = result.Outcome;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal Guid InventoryItemId { get; private set; }
    internal string CommandKind { get; private set; } = string.Empty;
    internal string IntentExpectedCurrentUnit { get; private set; } = string.Empty;
    internal string IntentNewUnit { get; private set; } = string.Empty;
    internal string ResultOperationalUnit { get; private set; } = string.Empty;
    internal string ResultOutcome { get; private set; } = string.Empty;

    internal bool Matches(
        Guid actorIdentityId,
        Guid inventoryItemId,
        string expectedCurrentUnit,
        string newUnit) =>
        ActorIdentityId == actorIdentityId &&
        InventoryItemId == inventoryItemId &&
        string.Equals(CommandKind, CommandKindValue, StringComparison.Ordinal) &&
        string.Equals(
            IntentExpectedCurrentUnit,
            expectedCurrentUnit,
            StringComparison.Ordinal) &&
        string.Equals(IntentNewUnit, newUnit, StringComparison.Ordinal);

    internal InventoryUnitCorrectionResponse ToResponse() =>
        new(InventoryItemId, ResultOperationalUnit, ResultOutcome);
}
