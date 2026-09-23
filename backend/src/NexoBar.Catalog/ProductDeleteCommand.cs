namespace NexoBar.Catalog;

internal sealed class ProductDeleteCommand
{
    private ProductDeleteCommand() { }

    internal ProductDeleteCommand(Guid key, Guid actor, Guid productId)
    {
        IdempotencyKey = key;
        ActorIdentityId = actor;
        ProductId = productId;
        CommandKind = CatalogCommandKind.DeleteProduct;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal Guid ProductId { get; private set; }
    internal CatalogCommandKind CommandKind { get; private set; }

    internal bool Matches(Guid actor, Guid productId) =>
        ActorIdentityId == actor && ProductId == productId && CommandKind == CatalogCommandKind.DeleteProduct;
}
