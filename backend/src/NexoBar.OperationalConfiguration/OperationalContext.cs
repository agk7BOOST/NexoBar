namespace NexoBar.OperationalConfiguration;

internal sealed class OperationalContext
{
    private OperationalContext() { }

    internal OperationalContext(Guid id, string operationalName)
    {
        Id = id;
        OperationalName = operationalName;
    }

    internal Guid Id { get; private set; }
    internal string OperationalName { get; private set; } = string.Empty;
    internal string NormalizedOperationalName { get; private set; } = string.Empty;
}
