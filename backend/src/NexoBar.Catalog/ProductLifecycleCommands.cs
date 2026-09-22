namespace NexoBar.Catalog;

internal sealed class GroupCreationCommand
{
    private GroupCreationCommand() { }
    internal GroupCreationCommand(Guid key, Guid actor, string name, Guid groupId)
    {
        IdempotencyKey = key; ActorIdentityId = actor; CommandKind = CatalogCommandKind.CreateGroup;
        IntentOperationalName = name; ResultGroupId = groupId;
    }
    internal Guid IdempotencyKey { get; private set; }
    internal Guid? ActorIdentityId { get; private set; }
    internal CatalogCommandKind CommandKind { get; private set; }
    internal string IntentOperationalName { get; private set; } = string.Empty;
    internal Guid ResultGroupId { get; private set; }
    internal bool Matches(Guid actor, string name) => ActorIdentityId == actor && CommandKind == CatalogCommandKind.CreateGroup && IntentOperationalName == name;
}

internal sealed class ProductGroupChangeCommand
{
    private ProductGroupChangeCommand() { }
    internal ProductGroupChangeCommand(Guid key, Guid actor, Guid productId, Guid? expected, Guid? next)
    {
        IdempotencyKey = key; ActorIdentityId = actor; ProductId = productId; CommandKind = CatalogCommandKind.ChangeProductGroup;
        IntentExpectedGroupId = expected; IntentNewGroupId = next; ResultGroupId = next;
    }
    internal Guid IdempotencyKey { get; private set; }
    internal Guid? ActorIdentityId { get; private set; }
    internal CatalogCommandKind CommandKind { get; private set; }
    internal Guid ProductId { get; private set; }
    internal Guid? IntentExpectedGroupId { get; private set; }
    internal Guid? IntentNewGroupId { get; private set; }
    internal Guid? ResultGroupId { get; private set; }
    internal bool Matches(Guid actor, Guid productId, Guid? expected, Guid? next) => ActorIdentityId == actor && CommandKind == CatalogCommandKind.ChangeProductGroup && ProductId == productId && IntentExpectedGroupId == expected && IntentNewGroupId == next;
}

internal sealed class ProductOperationalNameChangeCommand
{
    private ProductOperationalNameChangeCommand() { }
    internal ProductOperationalNameChangeCommand(Guid key, Guid actor, Guid productId, string expected, string next)
    {
        IdempotencyKey = key; ActorIdentityId = actor; ProductId = productId; CommandKind = CatalogCommandKind.ChangeProductOperationalName;
        IntentExpectedOperationalName = expected; IntentNewOperationalName = next; ResultOperationalName = next;
    }
    internal Guid IdempotencyKey { get; private set; }
    internal Guid? ActorIdentityId { get; private set; }
    internal CatalogCommandKind CommandKind { get; private set; }
    internal Guid ProductId { get; private set; }
    internal string IntentExpectedOperationalName { get; private set; } = string.Empty;
    internal string IntentNewOperationalName { get; private set; } = string.Empty;
    internal string ResultOperationalName { get; private set; } = string.Empty;
    internal bool Matches(Guid actor, Guid productId, string expected, string next) => ActorIdentityId == actor && CommandKind == CatalogCommandKind.ChangeProductOperationalName && ProductId == productId && IntentExpectedOperationalName == expected && IntentNewOperationalName == next;
}

internal sealed class ProductRetireCommand
{
    private ProductRetireCommand() { }
    internal ProductRetireCommand(Guid key, Guid actor, Guid productId, bool available)
    {
        IdempotencyKey = key; ActorIdentityId = actor; ProductId = productId; CommandKind = CatalogCommandKind.RetireProduct;
        ResultIsActive = false; ResultIsAvailable = available;
    }
    internal Guid IdempotencyKey { get; private set; }
    internal Guid? ActorIdentityId { get; private set; }
    internal CatalogCommandKind CommandKind { get; private set; }
    internal Guid ProductId { get; private set; }
    internal bool ResultIsActive { get; private set; }
    internal bool ResultIsAvailable { get; private set; }
    internal bool Matches(Guid actor, Guid productId) => ActorIdentityId == actor && ProductId == productId && CommandKind == CatalogCommandKind.RetireProduct;
}

internal sealed class ProductReactivateCommand
{
    private ProductReactivateCommand() { }
    internal ProductReactivateCommand(Guid key, Guid actor, Guid productId)
    {
        IdempotencyKey = key; ActorIdentityId = actor; ProductId = productId; CommandKind = CatalogCommandKind.ReactivateProduct;
        ResultIsActive = true; ResultIsAvailable = true;
    }
    internal Guid IdempotencyKey { get; private set; }
    internal Guid? ActorIdentityId { get; private set; }
    internal CatalogCommandKind CommandKind { get; private set; }
    internal Guid ProductId { get; private set; }
    internal bool ResultIsActive { get; private set; }
    internal bool ResultIsAvailable { get; private set; }
    internal bool Matches(Guid actor, Guid productId) => ActorIdentityId == actor && ProductId == productId && CommandKind == CatalogCommandKind.ReactivateProduct;
}
