using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CorrectDeliveryRequest(int Quantity);

internal sealed record DeliveryCorrectionResponse(
    Guid OrderId,
    Guid IncorporationId,
    int ContentOrdinal,
    Guid HistoryId,
    int CorrectedQuantity,
    int PreviousDeliveredQuantity,
    int ResultingDeliveredQuantity,
    DateTimeOffset OccurredAt);

internal sealed class DeliveryCorrectionHistory
{
    private DeliveryCorrectionHistory() { }

    internal DeliveryCorrectionHistory(DeliveryCorrectionResponse result, Guid actorIdentityId)
    {
        Id = result.HistoryId;
        OrderId = result.OrderId;
        IncorporationId = result.IncorporationId;
        ContentOrdinal = result.ContentOrdinal;
        CorrectedQuantity = result.CorrectedQuantity;
        PreviousDeliveredQuantity = result.PreviousDeliveredQuantity;
        ResultingDeliveredQuantity = result.ResultingDeliveredQuantity;
        OccurredAt = result.OccurredAt;
        ActorIdentityId = actorIdentityId;
    }

    internal Guid Id { get; private set; }
    internal string EventKind { get; private set; } = "DeliveryQuantityCorrected";
    internal Guid OrderId { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal int CorrectedQuantity { get; private set; }
    internal int PreviousDeliveredQuantity { get; private set; }
    internal int ResultingDeliveredQuantity { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
}

internal sealed class DeliveryCorrectionCommand
{
    private DeliveryCorrectionCommand() { }

    internal DeliveryCorrectionCommand(Guid key, Guid actorIdentityId, DeliveryCorrectionResponse result)
    {
        IdempotencyKey = key;
        ActorIdentityId = actorIdentityId;
        OrderId = result.OrderId;
        IncorporationId = result.IncorporationId;
        ContentOrdinal = result.ContentOrdinal;
        CorrectedQuantity = result.CorrectedQuantity;
        ResultHistoryId = result.HistoryId;
        PreviousDeliveredQuantity = result.PreviousDeliveredQuantity;
        ResultingDeliveredQuantity = result.ResultingDeliveredQuantity;
        OccurredAt = result.OccurredAt;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = "CorrectDeliveryQuantity";
    internal Guid OrderId { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal int CorrectedQuantity { get; private set; }
    internal Guid ResultHistoryId { get; private set; }
    internal int PreviousDeliveredQuantity { get; private set; }
    internal int ResultingDeliveredQuantity { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }

    internal bool Matches(Guid actor, Guid order, Guid incorporation, int ordinal, int quantity) =>
        ActorIdentityId == actor && OrderId == order && IncorporationId == incorporation &&
        ContentOrdinal == ordinal && CorrectedQuantity == quantity && CommandKind == "CorrectDeliveryQuantity";

    internal DeliveryCorrectionResponse ToResponse() => new(OrderId, IncorporationId, ContentOrdinal,
        ResultHistoryId, CorrectedQuantity, PreviousDeliveredQuantity, ResultingDeliveredQuantity, OccurredAt);
}
