namespace NexoBar.Catalog;

internal sealed class ProductPreparationConfigurationChangeCommand
{
    private ProductPreparationConfigurationChangeCommand() { }

    internal ProductPreparationConfigurationChangeCommand(
        Guid idempotencyKey,
        Guid productId,
        Guid? intentExpectedResponsibilityId,
        Guid? intentNewResponsibilityId)
    {
        IdempotencyKey = idempotencyKey;
        ProductId = productId;
        IntentExpectedResponsibilityId = intentExpectedResponsibilityId;
        IntentNewResponsibilityId = intentNewResponsibilityId;
        ResultResponsibilityId = intentNewResponsibilityId;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ProductId { get; private set; }
    internal Guid? IntentExpectedResponsibilityId { get; private set; }
    internal Guid? IntentNewResponsibilityId { get; private set; }
    internal Guid? ResultResponsibilityId { get; private set; }

    internal bool Matches(
        Guid productId,
        Guid? expectedResponsibilityId,
        Guid? newResponsibilityId) =>
        ProductId == productId &&
        IntentExpectedResponsibilityId == expectedResponsibilityId &&
        IntentNewResponsibilityId == newResponsibilityId;
}
