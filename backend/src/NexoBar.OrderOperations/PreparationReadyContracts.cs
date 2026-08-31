using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MarkPreparationQuantityReadyRequest(int Quantity);

internal sealed record MarkPreparationQuantityReadyResponse(
    Guid WorkId,
    Guid HistoryId,
    DateTimeOffset OccurredAt,
    int TotalQuantity,
    int PendingQuantity,
    int InPreparationQuantity,
    int ReadyQuantity);
