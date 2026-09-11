namespace NexoBar.OrderOperations;

internal sealed class OrderCancellationState
{
    private OrderCancellationState() { }
    internal OrderCancellationState(Guid orderId, Guid cancellationId) => (OrderId, CancellationId) = (orderId, cancellationId);
    internal Guid OrderId { get; private set; }
    internal Guid CancellationId { get; private set; }
}

internal sealed class CompleteCancellationHistory
{
    private CompleteCancellationHistory() { }
    internal CompleteCancellationHistory(Guid id, Guid orderId, Guid actor, DateTimeOffset occurredAt, bool discarded)
        => (Id, OrderId, ActorIdentityId, OccurredAt, PendingCompositionDiscarded) = (id, orderId, actor, occurredAt, discarded);
    internal Guid Id { get; private set; }
    internal Guid OrderId { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }
    internal bool PendingCompositionDiscarded { get; private set; }
    internal string EventKind { get; private set; } = "OrderCompletelyCancelled";
}

internal sealed class CompleteCancellationDetail
{
    private CompleteCancellationDetail() { }
    internal CompleteCancellationDetail(Guid cancellationId, CompleteCancellationConsequence consequence)
    {
        CancellationId = cancellationId;
        IncorporationId = consequence.IncorporationId;
        ContentOrdinal = consequence.ContentOrdinal;
        DirectOrPendingQuantity = consequence.DirectOrPendingQuantity;
        InPreparationQuantity = consequence.InPreparationQuantity;
        ReadyQuantity = consequence.ReadyQuantity;
    }
    internal Guid CancellationId { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal int DirectOrPendingQuantity { get; private set; }
    internal int InPreparationQuantity { get; private set; }
    internal int ReadyQuantity { get; private set; }
    internal int ResultingFulfillmentQuantity { get; private set; }
    internal CompleteCancellationConsequence ToResponse() => new(IncorporationId, ContentOrdinal,
        DirectOrPendingQuantity, InPreparationQuantity, ReadyQuantity, ResultingFulfillmentQuantity);
}

// The immutable semantic fact and its details are also the original durable result.
internal sealed class CompleteCancellationCommand
{
    internal const string Kind = "CompleteOrderCancellation";
    private CompleteCancellationCommand() { }
    internal CompleteCancellationCommand(Guid key, Guid cancellationId) => (IdempotencyKey, CancellationId) = (key, cancellationId);
    internal Guid IdempotencyKey { get; private set; }
    internal Guid CancellationId { get; private set; }
    internal string CommandKind { get; private set; } = Kind;
}

internal sealed record CompleteCancellationConsequence(Guid IncorporationId, int ContentOrdinal,
    int DirectOrPendingQuantity, int InPreparationQuantity, int ReadyQuantity, int ResultingFulfillmentQuantity);
internal sealed record CompleteCancellationResponse(Guid OrderId, Guid CancellationId, DateTimeOffset OccurredAt,
    bool IsCompletelyCancelled, bool PendingCompositionDiscarded, IReadOnlyList<CompleteCancellationConsequence> Consequences);
internal sealed record CompleteCancellationEvaluation(Guid OrderId, bool IsTerminal, bool IsCompletelyCancelled,
    Guid? CancellationId, DateTimeOffset? CancelledAt, bool HasEffectiveDelivery, bool HasPendingComposition,
    long? RemainingFulfillmentQuantity, bool? RequiresOperationalIntervention, bool IsEligible,
    IReadOnlyList<string> Blockers, IReadOnlyList<CompleteCancellationConsequence> Consequences);
internal sealed record CompleteCancellationResult(string? Error = null, CompleteCancellationResponse? Response = null,
    CompleteCancellationEvaluation? Evaluation = null);
