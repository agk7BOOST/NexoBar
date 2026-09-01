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
