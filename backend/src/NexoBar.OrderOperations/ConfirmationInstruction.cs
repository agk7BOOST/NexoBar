namespace NexoBar.OrderOperations;

internal static class ConfirmationInstruction
{
    internal static string? Canonicalize(string? instruction)
    {
        if (instruction is null)
        {
            return null;
        }

        var canonical = instruction
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        return canonical.Length == 0 ? null : canonical;
    }
}
