namespace NexoBar.IdentitiesAndCapabilities;

internal static class LoginIdentifierNormalizer
{
    internal static string? Normalize(string? loginIdentifier)
    {
        var trimmed = loginIdentifier?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? null
            : trimmed.ToUpperInvariant();
    }

    internal static string? Trim(string? loginIdentifier)
    {
        var trimmed = loginIdentifier?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
