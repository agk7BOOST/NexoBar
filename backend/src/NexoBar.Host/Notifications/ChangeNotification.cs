namespace NexoBar.Host.Notifications;

public sealed record ChangeNotificationScope
{
    private ChangeNotificationScope(Guid destinationId) => DestinationId = destinationId;

    public Guid DestinationId { get; }

    public static ChangeNotificationScope PreparationDestination(Guid destinationId)
    {
        if (destinationId == Guid.Empty)
        {
            throw new ArgumentException("A destination identifier is required.", nameof(destinationId));
        }

        return new(destinationId);
    }

    internal static bool TryParse(string? value, out ChangeNotificationScope? scope)
    {
        const string prefix = "preparation.destination:";
        scope = null;
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(value[prefix.Length..], "D", out var id) || id == Guid.Empty)
        {
            return false;
        }

        scope = PreparationDestination(id);
        return true;
    }
}

public sealed record ChangeNotification(ChangeNotificationScope Scope)
{
    public string Kind => "preparation.destination.changed";
}

/// <summary>
/// Process-local, best-effort publication. The caller owns commit timing.
/// Future module adapters are composed by the Host; modules need not reference the Host.
/// </summary>
public interface IChangeNotificationPublisher
{
    void Publish(ChangeNotification notification);
}
