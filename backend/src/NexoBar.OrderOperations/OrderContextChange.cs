namespace NexoBar.OrderOperations;

internal sealed class OrderContextChangeHistory
{
    private OrderContextChangeHistory() { }

    internal OrderContextChangeHistory(
        Guid id,
        Guid orderId,
        int sequence,
        Guid previousContextId,
        string previousContextOperationalName,
        Guid newContextId,
        string newContextOperationalName,
        Guid actorIdentityId,
        DateTimeOffset occurredAtUtc)
    {
        Id = id;
        OrderId = orderId;
        Sequence = sequence;
        PreviousContextId = previousContextId;
        PreviousContextOperationalName = previousContextOperationalName;
        NewContextId = newContextId;
        NewContextOperationalName = newContextOperationalName;
        ActorIdentityId = actorIdentityId;
        OccurredAtUtc = occurredAtUtc;
    }

    internal Guid Id { get; private set; }
    internal Guid OrderId { get; private set; }
    internal int Sequence { get; private set; }
    internal Guid PreviousContextId { get; private set; }
    internal string PreviousContextOperationalName { get; private set; } = string.Empty;
    internal Guid NewContextId { get; private set; }
    internal string NewContextOperationalName { get; private set; } = string.Empty;
    internal Guid ActorIdentityId { get; private set; }
    internal DateTimeOffset OccurredAtUtc { get; private set; }
}

internal sealed class OrderContextChangeCommand
{
    private OrderContextChangeCommand() { }

    internal OrderContextChangeCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid orderId,
        Guid expectedCurrentContextId,
        Guid newContextId,
        OrderContextChangeResponse result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        OrderId = orderId;
        ExpectedCurrentContextId = expectedCurrentContextId;
        NewContextId = newContextId;
        ResultPreviousContextId = result.PreviousContextId;
        ResultPreviousContextOperationalName = result.PreviousContextOperationalName;
        ResultCurrentContextId = result.CurrentContextId;
        ResultCurrentContextOperationalName = result.CurrentContextOperationalName;
        ResultOccurredAtUtc = result.OccurredAtUtc;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal Guid OrderId { get; private set; }
    internal Guid ExpectedCurrentContextId { get; private set; }
    internal Guid NewContextId { get; private set; }
    internal Guid ResultPreviousContextId { get; private set; }
    internal string ResultPreviousContextOperationalName { get; private set; } = string.Empty;
    internal Guid ResultCurrentContextId { get; private set; }
    internal string ResultCurrentContextOperationalName { get; private set; } = string.Empty;
    internal DateTimeOffset ResultOccurredAtUtc { get; private set; }

    internal bool Matches(Guid actorId, Guid orderId, Guid expectedId, Guid newId) =>
        ActorIdentityId == actorId && OrderId == orderId &&
        ExpectedCurrentContextId == expectedId && NewContextId == newId;

    internal OrderContextChangeResponse ToResponse() => new(
        OrderId,
        OrderId.ToString("D"),
        ResultPreviousContextId,
        ResultPreviousContextOperationalName,
        ResultCurrentContextId,
        ResultCurrentContextOperationalName,
        ResultOccurredAtUtc);
}

internal sealed record OrderContextChangeRequest(Guid ExpectedCurrentContextId, Guid NewContextId);

internal sealed record OrderContextChangeResponse(
    Guid OrderId,
    string OperationalReference,
    Guid PreviousContextId,
    string PreviousContextOperationalName,
    Guid CurrentContextId,
    string CurrentContextOperationalName,
    DateTimeOffset OccurredAtUtc);

internal sealed record OrderContextChangeResult(
    OrderContextChangeOutcome Outcome,
    OrderContextChangeResponse? Response = null);

internal enum OrderContextChangeOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden,
    OrderNotFound,
    TargetContextNotFound,
    NoChange,
    ExpectedContextStale,
    OrderFrozen,
    OrderClosed,
    OrderCancelled,
    IdempotencyConflict
}
