namespace NexoBar.OrderOperations;

internal sealed class PreparationHistory
{
    internal const string QuantityStartedEventKind = "PreparationQuantityStarted";

    private PreparationHistory() { }

    internal PreparationHistory(
        Guid id,
        Guid workId,
        int quantity,
        Guid actorIdentityId,
        DateTimeOffset occurredAt,
        int resultingTotalQuantity,
        int resultingPendingQuantity,
        int resultingInPreparationQuantity,
        int resultingReadyQuantity)
    {
        Id = id;
        WorkId = workId;
        EventKind = QuantityStartedEventKind;
        Quantity = quantity;
        ActorIdentityId = actorIdentityId;
        OccurredAt = occurredAt;
        ResultingTotalQuantity = resultingTotalQuantity;
        ResultingPendingQuantity = resultingPendingQuantity;
        ResultingInPreparationQuantity = resultingInPreparationQuantity;
        ResultingReadyQuantity = resultingReadyQuantity;
    }

    internal Guid Id { get; private set; }
    internal Guid WorkId { get; private set; }
    internal string EventKind { get; private set; } = string.Empty;
    internal int Quantity { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }
    internal int ResultingTotalQuantity { get; private set; }
    internal int ResultingPendingQuantity { get; private set; }
    internal int ResultingInPreparationQuantity { get; private set; }
    internal int ResultingReadyQuantity { get; private set; }
}

internal sealed class PreparationCommand
{
    internal const string StartQuantityCommandKind = "StartPreparationQuantity";

    private PreparationCommand() { }

    internal PreparationCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid workId,
        int quantity,
        StartPreparationQuantityResponse result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = StartQuantityCommandKind;
        WorkId = workId;
        Quantity = quantity;
        ResultHistoryId = result.HistoryId;
        ResultOccurredAt = result.OccurredAt;
        ResultTotalQuantity = result.TotalQuantity;
        ResultPendingQuantity = result.PendingQuantity;
        ResultInPreparationQuantity = result.InPreparationQuantity;
        ResultReadyQuantity = result.ReadyQuantity;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = string.Empty;
    internal Guid WorkId { get; private set; }
    internal int Quantity { get; private set; }
    internal Guid ResultHistoryId { get; private set; }
    internal DateTimeOffset ResultOccurredAt { get; private set; }
    internal int ResultTotalQuantity { get; private set; }
    internal int ResultPendingQuantity { get; private set; }
    internal int ResultInPreparationQuantity { get; private set; }
    internal int ResultReadyQuantity { get; private set; }

    internal bool Matches(
        Guid actorIdentityId,
        string commandKind,
        Guid workId,
        int quantity) =>
        ActorIdentityId == actorIdentityId &&
        string.Equals(CommandKind, commandKind, StringComparison.Ordinal) &&
        WorkId == workId &&
        Quantity == quantity;

    internal StartPreparationQuantityResponse ToStartResponse() => new(
        WorkId,
        ResultHistoryId,
        ResultOccurredAt,
        ResultTotalQuantity,
        ResultPendingQuantity,
        ResultInPreparationQuantity,
        ResultReadyQuantity);
}
