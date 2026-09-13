using NexoBar.OrderOperations;

namespace NexoBar.Host.Notifications;

internal sealed class OrderInvalidationPublisher(
    IChangeNotificationPublisher publisher,
    ILogger<OrderInvalidationPublisher> logger) : IOrderInvalidationPublisher
{
    public void PublishChanged(Guid orderId) =>
        Publish(orderId, terminal: false);

    public void PublishTerminalChanged(Guid orderId) =>
        Publish(orderId, terminal: true);

    private void Publish(Guid orderId, bool terminal)
    {
        try
        {
            publisher.Publish(terminal
                ? ChangeNotification.FinalOrderInvalidation(orderId)
                : new ChangeNotification(ChangeNotificationScope.ActiveOrder(orderId)));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Order invalidation publication failed after commit for {OrderId}.", orderId);
        }
    }
}
