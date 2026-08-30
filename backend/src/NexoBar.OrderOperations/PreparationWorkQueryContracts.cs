namespace NexoBar.OrderOperations;

internal sealed record PreparationWorkResponse(
    Guid WorkId,
    Guid PreparationResponsibilityId,
    string OperationalReference,
    string Context,
    Guid IncorporationId,
    int IncorporationOrdinal,
    Guid ProductId,
    string? Instruction,
    int TotalQuantity,
    int PendingQuantity,
    int InPreparationQuantity,
    int ReadyQuantity,
    DateTimeOffset ConfirmedAt);
