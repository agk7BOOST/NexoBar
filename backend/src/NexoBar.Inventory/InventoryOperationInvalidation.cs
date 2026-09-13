using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexoBar.Inventory;

/// <summary>
/// Best-effort invalidation of the current operational Inventory list.
/// Call only after a new authoritative State commit.
/// Implementations must contain publication failures so committed commands remain successful.
/// </summary>
public interface IInventoryOperationInvalidationPublisher
{
    void PublishChanged();
}

internal sealed class NullInventoryOperationInvalidationPublisher :
    IInventoryOperationInvalidationPublisher
{
    public void PublishChanged() { }
}

public static class InventoryOperationInvalidationRegistration
{
    public static IServiceCollection AddInventoryOperationInvalidationPublisher(
        this IServiceCollection services)
    {
        services.TryAddSingleton<IInventoryOperationInvalidationPublisher,
            NullInventoryOperationInvalidationPublisher>();
        return services;
    }
}
