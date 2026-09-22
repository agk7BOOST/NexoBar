namespace NexoBar.Catalog;

internal sealed class CatalogGroup
{
    private CatalogGroup() { }

    internal CatalogGroup(Guid id, string operationalName)
    {
        Id = id;
        OperationalName = operationalName;
    }

    internal Guid Id { get; private set; }
    internal string OperationalName { get; private set; } = string.Empty;
    internal string NormalizedOperationalName { get; private set; } = string.Empty;
}
