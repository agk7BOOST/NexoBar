namespace NexoBar.Inventory;

internal readonly record struct OperationalUnit
{
    internal const int MaximumLength = 100;

    private OperationalUnit(string value)
    {
        Value = value;
    }

    internal string Value { get; } = string.Empty;

    internal static bool TryCreate(string? rawValue, out OperationalUnit unit)
    {
        var value = rawValue?.Trim();
        if (string.IsNullOrEmpty(value) ||
            value.Length > MaximumLength ||
            ContainsLineBreak(value))
        {
            unit = default;
            return false;
        }

        unit = new OperationalUnit(value);
        return true;
    }

    internal static OperationalUnit FromPersisted(string value) => new(value);

    private static bool ContainsLineBreak(string value) =>
        value.Contains('\r', StringComparison.Ordinal) ||
        value.Contains('\n', StringComparison.Ordinal);
}
