using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StartPreparationQuantityRequest(int Quantity);

internal sealed record StartPreparationQuantityResponse(
    Guid WorkId,
    Guid HistoryId,
    DateTimeOffset OccurredAt,
    int TotalQuantity,
    int PendingQuantity,
    int InPreparationQuantity,
    int ReadyQuantity);
