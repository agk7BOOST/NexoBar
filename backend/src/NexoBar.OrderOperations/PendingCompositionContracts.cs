namespace NexoBar.OrderOperations;

internal sealed record PendingCompositionResponse(
    Guid PendingCompositionId,
    DateTimeOffset CreatedAt,
    Guid CreatedByIdentityId);

internal sealed record CurrentPendingCompositionResponse(
    Guid OrderId,
    PendingCompositionResponse? PendingComposition);
