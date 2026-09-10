using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationalInterventionRequest(int Quantity);

public sealed record OperationalInterventionResponse(
    Guid WorkId, Guid HistoryId, DateTimeOffset OccurredAt,
    int TotalQuantity, int PendingQuantity, int InPreparationQuantity, int ReadyQuantity);

public sealed record OperationalInterventionTargetResponse(
    Guid OrderId, Guid WorkId, Guid IncorporationId, int ContentOrdinal,
    Guid ProductId, string ProductOperationalName, string? Instruction,
    int ConfirmedQuantity, int RemovedByCorrectionQuantity, int CancelledQuantity, int FulfillmentQuantity,
    int PendingQuantity, int InPreparationQuantity, int ReadyQuantity, int TotalQuantity,
    int DeliveredQuantity, bool IsFrozen, int IntervenableInPreparationQuantity, int IntervenableReadyQuantity);
