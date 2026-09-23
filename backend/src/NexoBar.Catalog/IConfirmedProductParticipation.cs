using System.Data.Common;

namespace NexoBar.Catalog;

/// <summary>OrderOperations owns the durable fact of confirmed Product participation.</summary>
public interface IConfirmedProductParticipation
{
    Task<bool> HasConfirmedParticipationAsync(
        Guid productId, DbTransaction transaction, CancellationToken cancellationToken);
}
