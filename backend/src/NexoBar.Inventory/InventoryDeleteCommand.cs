namespace NexoBar.Inventory;

internal sealed class InventoryDeleteCommand
{
    internal const string CommandKindValue = "DeleteInventoryItem";

    private InventoryDeleteCommand() { }

    internal InventoryDeleteCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid inventoryItemId,
        InventoryItemDeleteResponse result,
        DateTimeOffset committedAtUtc)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        InventoryItemId = inventoryItemId;
        CommandKind = CommandKindValue;
        ResultItemId = result.ItemId;
        ResultDeleted = result.Deleted;
        CommittedAtUtc = committedAtUtc;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal Guid InventoryItemId { get; private set; }
    internal string CommandKind { get; private set; } = string.Empty;
    internal Guid ResultItemId { get; private set; }
    internal bool ResultDeleted { get; private set; }
    internal DateTimeOffset CommittedAtUtc { get; private set; }

    internal bool Matches(Guid actorIdentityId, Guid inventoryItemId) =>
        ActorIdentityId == actorIdentityId &&
        InventoryItemId == inventoryItemId &&
        string.Equals(CommandKind, CommandKindValue, StringComparison.Ordinal);

    internal InventoryItemDeleteResponse ToResponse() =>
        new(ResultItemId, ResultDeleted);
}
