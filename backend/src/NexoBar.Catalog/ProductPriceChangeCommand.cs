namespace NexoBar.Catalog;

internal sealed class ProductPriceChangeCommand
{
    private ProductPriceChangeCommand()
    {
    }

    internal ProductPriceChangeCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid productId,
        decimal expectedCurrentPrice,
        decimal newPrice)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = CatalogCommandKind.ChangeProductPrice;
        ProductId = productId;
        IntentExpectedCurrentPrice = expectedCurrentPrice;
        IntentNewPrice = newPrice;
        ResultPrice = newPrice;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid? ActorIdentityId { get; private set; }
    internal CatalogCommandKind CommandKind { get; private set; }

    internal Guid ProductId { get; private set; }

    internal decimal IntentExpectedCurrentPrice { get; private set; }

    internal decimal IntentNewPrice { get; private set; }

    internal decimal ResultPrice { get; private set; }

    internal bool Matches(
        Guid actorIdentityId,
        Guid productId,
        decimal expectedCurrentPrice,
        decimal newPrice) =>
        ActorIdentityId == actorIdentityId &&
        CommandKind == CatalogCommandKind.ChangeProductPrice &&
        ProductId == productId &&
        IntentExpectedCurrentPrice == expectedCurrentPrice &&
        IntentNewPrice == newPrice;
}
