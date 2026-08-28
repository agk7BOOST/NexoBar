namespace NexoBar.Catalog;

internal sealed class ProductCreationCommand
{
    private ProductCreationCommand()
    {
    }

    internal ProductCreationCommand(
        Guid idempotencyKey,
        string operationalName,
        decimal price,
        bool requiresPreparation,
        ProductResponse result)
    {
        IdempotencyKey = idempotencyKey;
        IntentOperationalName = operationalName;
        IntentPrice = price;
        IntentRequiresPreparation = requiresPreparation;
        ResultProductId = result.Id;
        ResultIsActive = result.IsActive;
        ResultIsAvailable = result.IsAvailable;
    }

    internal Guid IdempotencyKey { get; private set; }

    internal string IntentOperationalName { get; private set; } = string.Empty;

    internal decimal IntentPrice { get; private set; }

    internal bool IntentRequiresPreparation { get; private set; }

    internal Guid ResultProductId { get; private set; }

    internal bool ResultIsActive { get; private set; }

    internal bool ResultIsAvailable { get; private set; }

    internal bool Matches(
        string operationalName,
        decimal price,
        bool requiresPreparation) =>
        string.Equals(IntentOperationalName, operationalName, StringComparison.Ordinal) &&
        IntentPrice == price &&
        IntentRequiresPreparation == requiresPreparation;
}
