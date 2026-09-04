using System.Globalization;

namespace NexoBar.Inventory;

internal static class InventoryQuantity
{
    internal const int Precision = 28;
    internal const int Scale = 12;
    private const int MaximumIntegralDigits = Precision - Scale;

    internal static bool TryParseObserved(
        string? raw,
        out decimal quantity,
        out string canonical)
    {
        quantity = default;
        canonical = string.Empty;
        if (string.IsNullOrEmpty(raw) || raw.Length > Precision + 1)
        {
            return false;
        }

        var decimalPoint = raw.IndexOf('.');
        if (decimalPoint != raw.LastIndexOf('.'))
        {
            return false;
        }

        var integralLength = decimalPoint < 0 ? raw.Length : decimalPoint;
        var fractionalLength = decimalPoint < 0 ? 0 : raw.Length - decimalPoint - 1;
        if (integralLength is < 1 or > MaximumIntegralDigits ||
            fractionalLength > Scale ||
            (decimalPoint >= 0 && fractionalLength == 0))
        {
            return false;
        }

        foreach (var character in raw)
        {
            if (character != '.' && (character < '0' || character > '9'))
            {
                return false;
            }
        }

        if (!decimal.TryParse(
                raw,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out quantity) ||
            quantity < 0 ||
            quantity >= 10_000_000_000_000_000m)
        {
            quantity = default;
            return false;
        }

        canonical = Format(quantity);
        return true;
    }

    internal static bool TryParsePositive(
        string? raw,
        out decimal quantity,
        out string canonical)
    {
        if (!TryParseObserved(raw, out quantity, out canonical) || quantity <= 0)
        {
            quantity = default;
            canonical = string.Empty;
            return false;
        }

        return true;
    }

    internal static bool IsWithinStorageRange(decimal quantity) =>
        quantity > -10_000_000_000_000_000m &&
        quantity < 10_000_000_000_000_000m;

    internal static string Format(decimal quantity) =>
        quantity.ToString("0.############", CultureInfo.InvariantCulture);

    internal static string? Format(decimal? quantity) =>
        quantity is null ? null : Format(quantity.Value);
}
