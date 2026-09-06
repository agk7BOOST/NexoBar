namespace NexoBar.OrderOperations;

internal sealed record PreparationWorkResponse(
    Guid WorkId,
    Guid PreparationResponsibilityId,
    string OperationalReference,
    string Context,
    Guid IncorporationId,
    int IncorporationOrdinal,
    Guid ProductId,
    string ProductOperationalName,
    string? Instruction,
    int TotalQuantity,
    int PendingQuantity,
    int InPreparationQuantity,
    int ReadyQuantity,
    DateTimeOffset ConfirmedAt);

internal sealed record PreparationWorkQueryResult(
    PreparationWorkQueryOutcome Outcome,
    IReadOnlyList<PreparationWorkResponse>? Work)
{
    internal static PreparationWorkQueryResult Succeeded(
        IReadOnlyList<PreparationWorkResponse> work) =>
        new(PreparationWorkQueryOutcome.Succeeded, work);

    internal static PreparationWorkQueryResult Unauthenticated() =>
        new(PreparationWorkQueryOutcome.Unauthenticated, null);

    internal static PreparationWorkQueryResult Forbidden() =>
        new(PreparationWorkQueryOutcome.Forbidden, null);

    internal static PreparationWorkQueryResult ProductReferenceInconsistent() =>
        new(PreparationWorkQueryOutcome.ProductReferenceInconsistent, null);

    internal static PreparationWorkQueryResult StateInconsistent() =>
        new(PreparationWorkQueryOutcome.StateInconsistent, null);
}

internal enum PreparationWorkQueryOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden,
    ProductReferenceInconsistent,
    StateInconsistent
}
