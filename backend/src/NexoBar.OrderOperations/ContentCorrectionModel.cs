using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CorrectContentRequest(int Quantity);

internal sealed record ContentCorrectionResponse(
    Guid OrderId,
    Guid IncorporationId,
    int ContentOrdinal,
    Guid HistoryId,
    int CorrectedQuantity,
    int ConfirmedQuantity,
    int PreviousRemovedByCorrectionQuantity,
    int ResultingRemovedByCorrectionQuantity,
    int PreviousFulfillmentQuantity,
    int ResultingFulfillmentQuantity,
    DateTimeOffset OccurredAt);

internal sealed class ContentCorrectionHistory
{
    private ContentCorrectionHistory() { }

    internal ContentCorrectionHistory(ContentCorrectionResponse result, Guid actorIdentityId)
    {
        Id = result.HistoryId;
        OrderId = result.OrderId;
        IncorporationId = result.IncorporationId;
        ContentOrdinal = result.ContentOrdinal;
        CorrectedQuantity = result.CorrectedQuantity;
        ConfirmedQuantity = result.ConfirmedQuantity;
        PreviousRemovedByCorrectionQuantity = result.PreviousRemovedByCorrectionQuantity;
        ResultingRemovedByCorrectionQuantity = result.ResultingRemovedByCorrectionQuantity;
        PreviousFulfillmentQuantity = result.PreviousFulfillmentQuantity;
        ResultingFulfillmentQuantity = result.ResultingFulfillmentQuantity;
        OccurredAt = result.OccurredAt;
        ActorIdentityId = actorIdentityId;
    }

    internal Guid Id { get; private set; }
    internal string EventKind { get; private set; } = "ContentQuantityCorrected";
    internal Guid OrderId { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal int CorrectedQuantity { get; private set; }
    internal int ConfirmedQuantity { get; private set; }
    internal int PreviousRemovedByCorrectionQuantity { get; private set; }
    internal int ResultingRemovedByCorrectionQuantity { get; private set; }
    internal int PreviousFulfillmentQuantity { get; private set; }
    internal int ResultingFulfillmentQuantity { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
}

internal sealed class ContentCorrectionCommand
{
    private ContentCorrectionCommand() { }

    internal ContentCorrectionCommand(Guid key, Guid actorIdentityId, ContentCorrectionResponse result)
    {
        IdempotencyKey = key;
        ActorIdentityId = actorIdentityId;
        OrderId = result.OrderId;
        IncorporationId = result.IncorporationId;
        ContentOrdinal = result.ContentOrdinal;
        CorrectedQuantity = result.CorrectedQuantity;
        ResultHistoryId = result.HistoryId;
        ConfirmedQuantity = result.ConfirmedQuantity;
        PreviousRemovedByCorrectionQuantity = result.PreviousRemovedByCorrectionQuantity;
        ResultingRemovedByCorrectionQuantity = result.ResultingRemovedByCorrectionQuantity;
        PreviousFulfillmentQuantity = result.PreviousFulfillmentQuantity;
        ResultingFulfillmentQuantity = result.ResultingFulfillmentQuantity;
        OccurredAt = result.OccurredAt;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = "CorrectContentQuantity";
    internal Guid OrderId { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal int CorrectedQuantity { get; private set; }
    internal Guid ResultHistoryId { get; private set; }
    internal int ConfirmedQuantity { get; private set; }
    internal int PreviousRemovedByCorrectionQuantity { get; private set; }
    internal int ResultingRemovedByCorrectionQuantity { get; private set; }
    internal int PreviousFulfillmentQuantity { get; private set; }
    internal int ResultingFulfillmentQuantity { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }

    internal bool Matches(Guid actor, Guid order, Guid incorporation, int ordinal, int quantity) =>
        ActorIdentityId == actor && OrderId == order && IncorporationId == incorporation &&
        ContentOrdinal == ordinal && CorrectedQuantity == quantity && CommandKind == "CorrectContentQuantity";

    internal ContentCorrectionResponse ToResponse() => new(OrderId, IncorporationId, ContentOrdinal,
        ResultHistoryId, CorrectedQuantity, ConfirmedQuantity, PreviousRemovedByCorrectionQuantity, ResultingRemovedByCorrectionQuantity, PreviousFulfillmentQuantity, ResultingFulfillmentQuantity, OccurredAt);
}
