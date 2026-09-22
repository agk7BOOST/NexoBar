namespace NexoBar.Catalog;

internal sealed class ProductAvailabilityChangeCommand
{
    private ProductAvailabilityChangeCommand()
    {
    }

    internal ProductAvailabilityChangeCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid productId,
        bool expectedCurrentAvailability,
        bool newAvailability)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        ProductId = productId;
        CommandKind = CatalogCommandKind.ChangeProductAvailability;
        IntentExpectedCurrentAvailability = expectedCurrentAvailability;
        IntentNewAvailability = newAvailability;
        ResultAvailability = newAvailability;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid? ActorIdentityId { get; private set; }
    internal CatalogCommandKind CommandKind { get; private set; }
    internal Guid ProductId { get; private set; }
    internal bool IntentExpectedCurrentAvailability { get; private set; }
    internal bool IntentNewAvailability { get; private set; }
    internal bool ResultAvailability { get; private set; }

    internal bool Matches(
        Guid actorIdentityId,
        Guid productId,
        bool expectedCurrentAvailability,
        bool newAvailability) =>
        ActorIdentityId == actorIdentityId &&
        CommandKind == CatalogCommandKind.ChangeProductAvailability &&
        ProductId == productId &&
        IntentExpectedCurrentAvailability == expectedCurrentAvailability &&
        IntentNewAvailability == newAvailability;
}
