namespace NexoBar.Catalog;

internal sealed class Product
{
    private Product()
    {
    }

    internal Product(Guid id, string operationalName, decimal price)
    {
        Id = id;
        OperationalName = operationalName;
        Price = price;
        IsActive = true;
        IsAvailable = true;
        RequiresPreparation = false;
    }

    internal Guid Id { get; private set; }

    internal string OperationalName { get; private set; } = string.Empty;

    internal string NormalizedOperationalName { get; private set; } = string.Empty;

    internal decimal Price { get; private set; }

    internal bool IsActive { get; private set; }

    internal bool IsAvailable { get; private set; }

    internal bool RequiresPreparation { get; private set; }
}
