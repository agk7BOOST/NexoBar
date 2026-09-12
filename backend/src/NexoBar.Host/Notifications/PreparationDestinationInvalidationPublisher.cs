using Microsoft.Extensions.Logging;
using NexoBar.OrderOperations;

namespace NexoBar.Host.Notifications;

internal sealed class PreparationDestinationInvalidationPublisher(
    IChangeNotificationPublisher publisher,
    ILogger<PreparationDestinationInvalidationPublisher> logger) :
    IPreparationDestinationInvalidationPublisher
{
    public void Publish(IEnumerable<Guid> destinationIds)
    {
        foreach (var destinationId in destinationIds.Where(id => id != Guid.Empty).Distinct())
        {
            try
            {
                publisher.Publish(new ChangeNotification(
                    ChangeNotificationScope.PreparationDestination(destinationId)));
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Preparation destination invalidation publication failed after commit for {DestinationId}.",
                    destinationId);
            }
        }
    }
}
