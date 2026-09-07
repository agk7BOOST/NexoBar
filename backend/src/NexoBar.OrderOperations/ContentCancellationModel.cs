using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CancelContentRequest(int Quantity);

internal sealed record ContentCancellationResponse(
    Guid OrderId,
    Guid IncorporationId,
    int ContentOrdinal,
    Guid HistoryId,
    int CancelledQuantity,
    int ConfirmedQuantity,
    int PreviousCancelledQuantity,
    int ResultingCancelledQuantity,
    int PreviousFulfillmentQuantity,
    int ResultingFulfillmentQuantity,
    DateTimeOffset OccurredAt);

internal sealed class ContentCancellationHistory
{
    private ContentCancellationHistory() { }

    internal ContentCancellationHistory(ContentCancellationResponse result, Guid actorIdentityId)
    {
        Id = result.HistoryId;
        OrderId = result.OrderId;
        IncorporationId = result.IncorporationId;
        ContentOrdinal = result.ContentOrdinal;
        CancelledQuantity = result.CancelledQuantity;
        ConfirmedQuantity = result.ConfirmedQuantity;
        PreviousCancelledQuantity = result.PreviousCancelledQuantity;
        ResultingCancelledQuantity = result.ResultingCancelledQuantity;
        PreviousFulfillmentQuantity = result.PreviousFulfillmentQuantity;
        ResultingFulfillmentQuantity = result.ResultingFulfillmentQuantity;
        OccurredAt = result.OccurredAt;
        ActorIdentityId = actorIdentityId;
    }

    internal Guid Id { get; private set; }
    internal string EventKind { get; private set; } = "ContentQuantityCancelled";
    internal Guid OrderId { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal int CancelledQuantity { get; private set; }
    internal int ConfirmedQuantity { get; private set; }
    internal int PreviousCancelledQuantity { get; private set; }
    internal int ResultingCancelledQuantity { get; private set; }
    internal int PreviousFulfillmentQuantity { get; private set; }
    internal int ResultingFulfillmentQuantity { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
}

internal sealed class ContentCancellationCommand
{
    private ContentCancellationCommand() { }

    internal ContentCancellationCommand(Guid key, Guid actorIdentityId, ContentCancellationResponse result)
    {
        IdempotencyKey = key;
        ActorIdentityId = actorIdentityId;
        OrderId = result.OrderId;
        IncorporationId = result.IncorporationId;
        ContentOrdinal = result.ContentOrdinal;
        CancelledQuantity = result.CancelledQuantity;
        ResultHistoryId = result.HistoryId;
        ConfirmedQuantity = result.ConfirmedQuantity;
        PreviousCancelledQuantity = result.PreviousCancelledQuantity;
        ResultingCancelledQuantity = result.ResultingCancelledQuantity;
        PreviousFulfillmentQuantity = result.PreviousFulfillmentQuantity;
        ResultingFulfillmentQuantity = result.ResultingFulfillmentQuantity;
        OccurredAt = result.OccurredAt;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = "CancelContentQuantity";
    internal Guid OrderId { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal int CancelledQuantity { get; private set; }
    internal Guid ResultHistoryId { get; private set; }
    internal int ConfirmedQuantity { get; private set; }
    internal int PreviousCancelledQuantity { get; private set; }
    internal int ResultingCancelledQuantity { get; private set; }
    internal int PreviousFulfillmentQuantity { get; private set; }
    internal int ResultingFulfillmentQuantity { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }

    internal bool Matches(Guid actor, Guid order, Guid incorporation, int ordinal, int quantity) =>
        ActorIdentityId == actor && OrderId == order && IncorporationId == incorporation &&
        ContentOrdinal == ordinal && CancelledQuantity == quantity && CommandKind == "CancelContentQuantity";

    internal ContentCancellationResponse ToResponse() => new(OrderId, IncorporationId, ContentOrdinal,
        ResultHistoryId, CancelledQuantity, ConfirmedQuantity, PreviousCancelledQuantity, ResultingCancelledQuantity, PreviousFulfillmentQuantity, ResultingFulfillmentQuantity, OccurredAt);
}
