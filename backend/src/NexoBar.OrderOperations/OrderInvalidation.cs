using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexoBar.OrderOperations;

/// <summary>
/// Best-effort invalidation of current Order reads. Call only after a new State commit.
/// Implementations must contain publication failures so committed commands remain successful.
/// </summary>
public interface IOrderInvalidationPublisher
{
    void PublishChanged(Guid orderId);

    /// <summary>Only Closure and Complete Order Cancellation may publish terminal changes.</summary>
    void PublishTerminalChanged(Guid orderId);
}

internal sealed class NullOrderInvalidationPublisher : IOrderInvalidationPublisher
{
    public void PublishChanged(Guid orderId) { }
    public void PublishTerminalChanged(Guid orderId) { }
}

public static class OrderInvalidationRegistration
{
    public static IServiceCollection AddOrderInvalidationPublisher(this IServiceCollection services)
    {
        services.TryAddSingleton<IOrderInvalidationPublisher, NullOrderInvalidationPublisher>();
        return services;
    }
}
