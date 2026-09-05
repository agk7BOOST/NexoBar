namespace NexoBar.OrderOperations;

internal sealed class Closure
{
    private Closure() { }

    internal Closure(Guid id, Guid orderId, DateTimeOffset closedAt, Guid actorIdentityId)
    {
        Id = id;
        OrderId = orderId;
        ClosedAt = closedAt;
        ActorIdentityId = actorIdentityId;
    }

    internal Guid Id { get; private set; }
    internal Guid OrderId { get; private set; }
    internal DateTimeOffset ClosedAt { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
}

internal sealed class ClosureHistory
{
    private ClosureHistory() { }

    internal ClosureHistory(Guid id, Closure closure)
    {
        Id = id;
        ClosureId = closure.Id;
        OrderId = closure.OrderId;
        OccurredAt = closure.ClosedAt;
        ActorIdentityId = closure.ActorIdentityId;
    }

    internal Guid Id { get; private set; }
    internal Guid ClosureId { get; private set; }
    internal Guid OrderId { get; private set; }
    internal string EventKind { get; private set; } = "Closed";
    internal DateTimeOffset OccurredAt { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
}

internal sealed class ClosureCommand
{
    internal const string CloseCommandKind = "CloseOrder";
    private ClosureCommand() { }

    internal ClosureCommand(Guid idempotencyKey, Closure closure)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = closure.ActorIdentityId;
        OrderId = closure.OrderId;
        ResultClosureId = closure.Id;
        ResultClosedAt = closure.ClosedAt;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = CloseCommandKind;
    internal Guid OrderId { get; private set; }
    internal Guid ResultClosureId { get; private set; }
    internal DateTimeOffset ResultClosedAt { get; private set; }

    internal bool Matches(Guid actorIdentityId, Guid orderId) =>
        ActorIdentityId == actorIdentityId && OrderId == orderId && CommandKind == CloseCommandKind;

    internal ClosureResponse ToResponse() => new(ResultClosureId, OrderId, ResultClosedAt, true);
}

internal sealed record ClosureResponse(Guid ClosureId, Guid OrderId, DateTimeOffset ClosedAt, bool IsClosed);
