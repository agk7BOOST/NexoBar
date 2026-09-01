using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DeliverQuantityRequest(int Quantity);

internal sealed record DeliverQuantityResponse(
    Guid IncorporationId,
    int ContentOrdinal,
    Guid HistoryId,
    DateTimeOffset OccurredAt,
    int DeliveredQuantity);
