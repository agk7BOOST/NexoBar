namespace NexoBar.Catalog;

internal sealed class ProductPriceChangeCommand
{
    private ProductPriceChangeCommand()
    {
    }

    internal ProductPriceChangeCommand(
        Guid idempotencyKey,
        Guid productId,
        decimal expectedCurrentPrice,
        decimal newPrice)
    {
        IdempotencyKey = idempotencyKey;
        ProductId = productId;
        IntentExpectedCurrentPrice = expectedCurrentPrice;
        IntentNewPrice = newPrice;
        ResultPrice = newPrice;
    }

    internal Guid IdempotencyKey { get; private set; }

    internal Guid ProductId { get; private set; }

    internal decimal IntentExpectedCurrentPrice { get; private set; }

    internal decimal IntentNewPrice { get; private set; }

    internal decimal ResultPrice { get; private set; }

    internal bool Matches(Guid productId, decimal expectedCurrentPrice, decimal newPrice) =>
        ProductId == productId &&
        IntentExpectedCurrentPrice == expectedCurrentPrice &&
        IntentNewPrice == newPrice;
}
