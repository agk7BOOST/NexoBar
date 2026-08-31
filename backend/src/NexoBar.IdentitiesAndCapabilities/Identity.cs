namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class Identity
{
    private Identity()
    {
    }

    internal Identity(string operationalName, bool isActive)
    {
        var normalizedInput = operationalName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedInput))
        {
            throw new ArgumentException(
                "Operational name must contain non-whitespace text.",
                nameof(operationalName));
        }

        Id = Guid.CreateVersion7();
        OperationalName = normalizedInput;
        IsActive = isActive;
    }

    internal Guid Id { get; private set; }

    internal string OperationalName { get; private set; } = string.Empty;

    internal string NormalizedOperationalName { get; private set; } = string.Empty;

    internal bool IsActive { get; private set; }

    internal ICollection<ResponsibilityAssignment> ResponsibilityAssignments { get; } =
        new List<ResponsibilityAssignment>();

    internal ICollection<PreparationEnablement> PreparationEnablements { get; } =
        new List<PreparationEnablement>();
}
