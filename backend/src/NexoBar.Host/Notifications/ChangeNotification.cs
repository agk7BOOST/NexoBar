namespace NexoBar.Host.Notifications;

public sealed record ChangeNotificationScope
{
    private ChangeNotificationScope(ChangeNotificationScopeKind kind, Guid scopeId)
    {
        Kind = kind;
        ScopeId = scopeId;
    }

    internal ChangeNotificationScopeKind Kind { get; }
    public Guid ScopeId { get; }
    public Guid DestinationId => Kind == ChangeNotificationScopeKind.PreparationDestination
        ? ScopeId : throw new InvalidOperationException("This scope is not a Preparation destination.");

    public static ChangeNotificationScope PreparationDestination(Guid destinationId)
    {
        if (destinationId == Guid.Empty)
        {
            throw new ArgumentException("A destination identifier is required.", nameof(destinationId));
        }

        return new(ChangeNotificationScopeKind.PreparationDestination, destinationId);
    }

    public static ChangeNotificationScope ActiveOrder(Guid orderId)
    {
        if (orderId == Guid.Empty) throw new ArgumentException("An Order identifier is required.", nameof(orderId));
        return new(ChangeNotificationScopeKind.ActiveOrder, orderId);
    }

    internal static bool TryParse(string? value, out ChangeNotificationScope? scope)
    {
        scope = null;
        var parts = value?.Split(':');
        if (parts is not { Length: 2 } || !Guid.TryParseExact(parts[1], "D", out var id) || id == Guid.Empty) return false;
        scope = parts[0] switch
        {
            "preparation.destination" => PreparationDestination(id),
            "order.active" => ActiveOrder(id),
            _ => null
        };
        return scope is not null;
    }
}

internal enum ChangeNotificationScopeKind { PreparationDestination, ActiveOrder }
internal enum ChangeNotificationDelivery { Normal, FinalForPreviouslyAuthorizedScope }

public sealed record ChangeNotification
{
    public ChangeNotification(ChangeNotificationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Scope = scope;
    }

    public ChangeNotificationScope Scope { get; }
    public string Kind => Scope.Kind == ChangeNotificationScopeKind.ActiveOrder
        ? "order.changed" : "preparation.destination.changed";
    internal ChangeNotificationDelivery Delivery { get; private init; }

    // Trusted Host-side classification only. S8-I4B will use this solely after
    // Closure/Complete Cancellation commit. It is never accepted from a client.
    internal static ChangeNotification FinalOrderInvalidation(Guid orderId) =>
        new(ChangeNotificationScope.ActiveOrder(orderId)) { Delivery = ChangeNotificationDelivery.FinalForPreviouslyAuthorizedScope };
}

/// <summary>
/// Process-local, best-effort publication. The caller owns commit timing.
/// Future module adapters are composed by the Host; modules need not reference the Host.
/// </summary>
public interface IChangeNotificationPublisher
{
    void Publish(ChangeNotification notification);
}
