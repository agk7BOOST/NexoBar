namespace NexoBar.Inventory;

internal sealed class InventoryItemCreationCommand
{
    internal const string CreateItemCommandKind = "CreateInventoryItem";

    private InventoryItemCreationCommand()
    {
    }

    internal InventoryItemCreationCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        string intentNormalizedOperationalName,
        string intentOperationalUnit,
        InventoryItemResponse result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = CreateItemCommandKind;
        IntentNormalizedOperationalName = intentNormalizedOperationalName;
        IntentOperationalUnit = intentOperationalUnit;
        ResultItemId = result.ItemId;
        ResultOperationalName = result.OperationalName;
        ResultOperationalUnit = result.OperationalUnit;
        ResultCurrentRegisteredQuantity = null;
        ResultMovementRevision = result.MovementRevision;
    }

    internal Guid IdempotencyKey { get; private set; }

    internal Guid ActorIdentityId { get; private set; }

    internal string CommandKind { get; private set; } = string.Empty;

    internal string IntentNormalizedOperationalName { get; private set; } = string.Empty;

    internal string IntentOperationalUnit { get; private set; } = string.Empty;

    internal Guid ResultItemId { get; private set; }

    internal string ResultOperationalName { get; private set; } = string.Empty;

    internal string ResultOperationalUnit { get; private set; } = string.Empty;

    internal decimal? ResultCurrentRegisteredQuantity { get; private set; }

    internal long ResultMovementRevision { get; private set; }

    internal bool Matches(
        Guid actorIdentityId,
        string normalizedOperationalName,
        string operationalUnit) =>
        ActorIdentityId == actorIdentityId &&
        string.Equals(
            CommandKind,
            CreateItemCommandKind,
            StringComparison.Ordinal) &&
        string.Equals(
            IntentNormalizedOperationalName,
            normalizedOperationalName,
            StringComparison.Ordinal) &&
        string.Equals(
            IntentOperationalUnit,
            operationalUnit,
            StringComparison.Ordinal);
}
