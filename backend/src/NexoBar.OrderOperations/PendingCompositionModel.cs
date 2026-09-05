namespace NexoBar.OrderOperations;

internal sealed class PendingComposition
{
    private PendingComposition() { }

    internal PendingComposition(
        Guid id,
        Guid orderId,
        DateTimeOffset createdAt,
        Guid createdByIdentityId)
    {
        Id = id;
        OrderId = orderId;
        CreatedAt = createdAt;
        CreatedByIdentityId = createdByIdentityId;
    }

    internal Guid Id { get; private set; }
    internal Guid OrderId { get; private set; }
    internal DateTimeOffset CreatedAt { get; private set; }
    internal Guid CreatedByIdentityId { get; private set; }
}

internal sealed class PendingCompositionCommand
{
    internal const string StartCommandKind = "StartPendingComposition";
    internal const string DiscardCommandKind = "DiscardPendingComposition";

    private PendingCompositionCommand() { }

    private PendingCompositionCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        string commandKind,
        Guid orderId,
        Guid? intentPendingCompositionId,
        Guid resultPendingCompositionId,
        DateTimeOffset resultCreatedAt,
        Guid resultCreatedByIdentityId)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = commandKind;
        OrderId = orderId;
        IntentPendingCompositionId = intentPendingCompositionId;
        ResultPendingCompositionId = resultPendingCompositionId;
        ResultCreatedAt = resultCreatedAt;
        ResultCreatedByIdentityId = resultCreatedByIdentityId;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = string.Empty;
    internal Guid OrderId { get; private set; }
    internal Guid? IntentPendingCompositionId { get; private set; }
    internal Guid ResultPendingCompositionId { get; private set; }
    internal DateTimeOffset ResultCreatedAt { get; private set; }
    internal Guid ResultCreatedByIdentityId { get; private set; }

    internal static PendingCompositionCommand Start(
        Guid idempotencyKey,
        Guid actorIdentityId,
        PendingComposition pendingComposition) =>
        new(
            idempotencyKey,
            actorIdentityId,
            StartCommandKind,
            pendingComposition.OrderId,
            null,
            pendingComposition.Id,
            pendingComposition.CreatedAt,
            pendingComposition.CreatedByIdentityId);

    internal static PendingCompositionCommand Discard(
        Guid idempotencyKey,
        Guid actorIdentityId,
        PendingComposition pendingComposition) =>
        new(
            idempotencyKey,
            actorIdentityId,
            DiscardCommandKind,
            pendingComposition.OrderId,
            pendingComposition.Id,
            pendingComposition.Id,
            pendingComposition.CreatedAt,
            pendingComposition.CreatedByIdentityId);

    internal bool MatchesStart(Guid actorIdentityId, Guid orderId) =>
        ActorIdentityId == actorIdentityId &&
        CommandKind == StartCommandKind &&
        OrderId == orderId &&
        IntentPendingCompositionId is null;

    internal bool MatchesDiscard(
        Guid actorIdentityId,
        Guid orderId,
        Guid pendingCompositionId) =>
        ActorIdentityId == actorIdentityId &&
        CommandKind == DiscardCommandKind &&
        OrderId == orderId &&
        IntentPendingCompositionId == pendingCompositionId;

    internal PendingCompositionResponse ToResponse() =>
        new(ResultPendingCompositionId, ResultCreatedAt, ResultCreatedByIdentityId);
}
