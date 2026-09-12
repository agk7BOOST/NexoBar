using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexoBar.OrderOperations;

/// <summary>
/// Best-effort, post-commit invalidation of authoritative Preparation reads.
/// </summary>
public interface IPreparationDestinationInvalidationPublisher
{
    void Publish(IEnumerable<Guid> destinationIds);
}

internal sealed class NullPreparationDestinationInvalidationPublisher :
    IPreparationDestinationInvalidationPublisher
{
    public void Publish(IEnumerable<Guid> destinationIds)
    {
    }
}

public static class PreparationDestinationInvalidationRegistration
{
    public static IServiceCollection AddPreparationDestinationInvalidationPublisher(
        this IServiceCollection services)
    {
        services.TryAddSingleton<IPreparationDestinationInvalidationPublisher,
            NullPreparationDestinationInvalidationPublisher>();
        return services;
    }
}
