using NexoBar.Inventory;

namespace NexoBar.Host.Notifications;

internal sealed class InventoryOperationInvalidationPublisher(
    IChangeNotificationPublisher publisher,
    ILogger<InventoryOperationInvalidationPublisher> logger) :
    IInventoryOperationInvalidationPublisher
{
    public void PublishChanged()
    {
        try
        {
            publisher.Publish(new ChangeNotification(
                ChangeNotificationScope.InventoryOperation()));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Inventory operation invalidation publication failed after commit.");
        }
    }
}
