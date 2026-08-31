namespace NexoBar.OrderOperations;

internal sealed class PreparationHistory
{
    internal const string QuantityStartedEventKind = "PreparationQuantityStarted";
    internal const string QuantityReadyEventKind = "PreparationQuantityReady";

    private PreparationHistory() { }

    private PreparationHistory(
        Guid id,
        Guid workId,
        string eventKind,
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
        EventKind = eventKind;
        Quantity = quantity;
        ActorIdentityId = actorIdentityId;
        OccurredAt = occurredAt;
        ResultingTotalQuantity = resultingTotalQuantity;
        ResultingPendingQuantity = resultingPendingQuantity;
        ResultingInPreparationQuantity = resultingInPreparationQuantity;
        ResultingReadyQuantity = resultingReadyQuantity;
    }

    internal static PreparationHistory QuantityStarted(
        Guid id,
        Guid workId,
        int quantity,
        Guid actorIdentityId,
        DateTimeOffset occurredAt,
        PreparationCommandResult result) =>
        Create(
            id,
            workId,
            QuantityStartedEventKind,
            quantity,
            actorIdentityId,
            occurredAt,
            result);

    internal static PreparationHistory QuantityReady(
        Guid id,
        Guid workId,
        int quantity,
        Guid actorIdentityId,
        DateTimeOffset occurredAt,
        PreparationCommandResult result) =>
        Create(
            id,
            workId,
            QuantityReadyEventKind,
            quantity,
            actorIdentityId,
            occurredAt,
            result);

    private static PreparationHistory Create(
        Guid id,
        Guid workId,
        string eventKind,
        int quantity,
        Guid actorIdentityId,
        DateTimeOffset occurredAt,
        PreparationCommandResult result) =>
        new(
            id,
            workId,
            eventKind,
            quantity,
            actorIdentityId,
            occurredAt,
            result.TotalQuantity,
            result.PendingQuantity,
            result.InPreparationQuantity,
            result.ReadyQuantity);

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
    internal const string MarkQuantityReadyCommandKind =
        "MarkPreparationQuantityReady";

    private PreparationCommand() { }

    private PreparationCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        string commandKind,
        Guid workId,
        int quantity,
        PreparationCommandResult result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = commandKind;
        WorkId = workId;
        Quantity = quantity;
        ResultHistoryId = result.HistoryId;
        ResultOccurredAt = result.OccurredAt;
        ResultTotalQuantity = result.TotalQuantity;
        ResultPendingQuantity = result.PendingQuantity;
        ResultInPreparationQuantity = result.InPreparationQuantity;
        ResultReadyQuantity = result.ReadyQuantity;
    }

    internal static PreparationCommand StartQuantity(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid workId,
        int quantity,
        PreparationCommandResult result) =>
        new(
            idempotencyKey,
            actorIdentityId,
            StartQuantityCommandKind,
            workId,
            quantity,
            result);

    internal static PreparationCommand MarkQuantityReady(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid workId,
        int quantity,
        PreparationCommandResult result) =>
        new(
            idempotencyKey,
            actorIdentityId,
            MarkQuantityReadyCommandKind,
            workId,
            quantity,
            result);

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

    internal PreparationCommandResult ToResult() => new(
        WorkId,
        ResultHistoryId,
        ResultOccurredAt,
        ResultTotalQuantity,
        ResultPendingQuantity,
        ResultInPreparationQuantity,
        ResultReadyQuantity);
}

internal sealed record PreparationCommandResult(
    Guid WorkId,
    Guid HistoryId,
    DateTimeOffset OccurredAt,
    int TotalQuantity,
    int PendingQuantity,
    int InPreparationQuantity,
    int ReadyQuantity);
