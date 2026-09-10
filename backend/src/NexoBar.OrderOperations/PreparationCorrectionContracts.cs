using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CorrectPreparationProgressRequest(int Quantity);

internal sealed record PreparationCorrectionResponse(
    Guid WorkId, Guid HistoryId, DateTimeOffset OccurredAt, int TotalQuantity,
    int PendingQuantity, int InPreparationQuantity, int ReadyQuantity);
