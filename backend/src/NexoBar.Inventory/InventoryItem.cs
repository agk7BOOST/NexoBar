namespace NexoBar.Inventory;

internal sealed class InventoryItem
{
    internal const int OperationalNameMaximumLength = 200;

    private InventoryItem()
    {
    }

    private InventoryItem(
        Guid id,
        string operationalName,
        string normalizedOperationalName,
        OperationalUnit operationalUnit)
    {
        Id = id;
        OperationalName = operationalName;
        NormalizedOperationalName = normalizedOperationalName;
        OperationalUnit = operationalUnit;
        CurrentRegisteredQuantity = null;
        MovementRevision = 0;
    }

    internal Guid Id { get; private set; }

    internal string OperationalName { get; private set; } = string.Empty;

    internal string NormalizedOperationalName { get; private set; } = string.Empty;

    internal OperationalUnit OperationalUnit { get; private set; }

    internal decimal? CurrentRegisteredQuantity { get; private set; }

    internal long MovementRevision { get; private set; }

    internal InventoryReconciliationTransition Reconcile(decimal observedQuantity)
    {
        if (observedQuantity < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedQuantity),
                "An observed physical quantity cannot be negative.");
        }

        var previousQuantity = CurrentRegisteredQuantity;
        if (previousQuantity is not null && previousQuantity.Value == observedQuantity)
        {
            return InventoryReconciliationTransition.NoDiscrepancy(
                previousQuantity.Value,
                MovementRevision);
        }

        var resultingRevision = checked(MovementRevision + 1);
        CurrentRegisteredQuantity = observedQuantity;
        MovementRevision = resultingRevision;
        return InventoryReconciliationTransition.Reconciled(
            previousQuantity,
            observedQuantity,
            resultingRevision);
    }

    internal static InventoryItemValidation TryCreate(
        string? rawOperationalName,
        string? rawOperationalUnit)
    {
        var operationalName = Trim(rawOperationalName);
        var normalizedOperationalName = NormalizeOperationalName(operationalName);
        if (!IsValidOperationalName(operationalName) ||
            normalizedOperationalName.Length > OperationalNameMaximumLength)
        {
            return InventoryItemValidation.InvalidName();
        }

        if (!OperationalUnit.TryCreate(rawOperationalUnit, out var operationalUnit))
        {
            return InventoryItemValidation.InvalidUnit();
        }

        return InventoryItemValidation.Valid(new InventoryItem(
            Guid.CreateVersion7(),
            operationalName,
            normalizedOperationalName,
            operationalUnit));
    }

    internal static string CanonicalNameFingerprint(string? value) =>
        NormalizeOperationalName(Trim(value));

    internal static string CanonicalUnitFingerprint(string? value) => Trim(value);

    internal static string NormalizeOperationalName(string value) =>
        value.ToUpperInvariant();

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;

    private static bool IsValidOperationalName(string value) =>
        value.Length is > 0 and <= OperationalNameMaximumLength &&
        !value.Contains('\r', StringComparison.Ordinal) &&
        !value.Contains('\n', StringComparison.Ordinal);
}

internal sealed record InventoryReconciliationTransition(
    bool MovementRequired,
    decimal? PreviousRegisteredQuantity,
    decimal ObservedQuantity,
    decimal? Difference,
    decimal ResultingRegisteredQuantity,
    long MovementRevision)
{
    internal static InventoryReconciliationTransition NoDiscrepancy(
        decimal quantity,
        long revision) =>
        new(false, quantity, quantity, 0m, quantity, revision);

    internal static InventoryReconciliationTransition Reconciled(
        decimal? previousQuantity,
        decimal observedQuantity,
        long revision) =>
        new(
            true,
            previousQuantity,
            observedQuantity,
            previousQuantity is null ? null : observedQuantity - previousQuantity.Value,
            observedQuantity,
            revision);
}

internal sealed record InventoryItemValidation(
    InventoryItem? Item,
    InventoryItemValidationError? Error)
{
    internal static InventoryItemValidation Valid(InventoryItem item) =>
        new(item, null);

    internal static InventoryItemValidation InvalidName() =>
        new(null, InventoryItemValidationError.OperationalNameInvalid);

    internal static InventoryItemValidation InvalidUnit() =>
        new(null, InventoryItemValidationError.OperationalUnitInvalid);
}

internal enum InventoryItemValidationError
{
    OperationalNameInvalid,
    OperationalUnitInvalid
}
