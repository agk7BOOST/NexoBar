namespace NexoBar.Catalog;

internal sealed class ProductPreparationConfigurationChangeCommand
{
    private ProductPreparationConfigurationChangeCommand() { }

    internal ProductPreparationConfigurationChangeCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid productId,
        Guid? intentExpectedResponsibilityId,
        Guid? intentNewResponsibilityId)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = CatalogCommandKind.ChangeProductPreparationConfiguration;
        ProductId = productId;
        IntentExpectedResponsibilityId = intentExpectedResponsibilityId;
        IntentNewResponsibilityId = intentNewResponsibilityId;
        ResultResponsibilityId = intentNewResponsibilityId;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid? ActorIdentityId { get; private set; }
    internal CatalogCommandKind CommandKind { get; private set; }
    internal Guid ProductId { get; private set; }
    internal Guid? IntentExpectedResponsibilityId { get; private set; }
    internal Guid? IntentNewResponsibilityId { get; private set; }
    internal Guid? ResultResponsibilityId { get; private set; }

    internal bool Matches(
        Guid actorIdentityId,
        Guid productId,
        Guid? expectedResponsibilityId,
        Guid? newResponsibilityId) =>
        ActorIdentityId == actorIdentityId &&
        CommandKind == CatalogCommandKind.ChangeProductPreparationConfiguration &&
        ProductId == productId &&
        IntentExpectedResponsibilityId == expectedResponsibilityId &&
        IntentNewResponsibilityId == newResponsibilityId;
}

internal enum CatalogCommandKind
{
    CreateProduct,
    ChangeProductPrice,
    ChangeProductPreparationConfiguration
}
