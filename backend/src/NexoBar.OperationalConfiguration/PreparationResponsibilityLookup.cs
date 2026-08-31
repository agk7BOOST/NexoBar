using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NexoBar.OperationalConfiguration;

public interface IPreparationResponsibilityLookup
{
    Task<bool> ExistsAsync(Guid responsibilityId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PreparationResponsibilityReference>> ReadByIdsAsync(
        IReadOnlyCollection<Guid> responsibilityIds,
        DbTransaction transaction,
        CancellationToken cancellationToken);
}

public sealed record PreparationResponsibilityReference(
    Guid Id,
    string OperationalName);

internal sealed class PreparationResponsibilityLookup(
    OperationalConfigurationDbContext dbContext) : IPreparationResponsibilityLookup
{
    public Task<bool> ExistsAsync(
        Guid responsibilityId,
        CancellationToken cancellationToken) =>
        dbContext.PreparationResponsibilities
            .AsNoTracking()
            .AnyAsync(
                responsibility => responsibility.Id == responsibilityId,
                cancellationToken);

    public async Task<IReadOnlyList<PreparationResponsibilityReference>> ReadByIdsAsync(
        IReadOnlyCollection<Guid> responsibilityIds,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var connection = transaction.Connection
            ?? throw new InvalidOperationException(
                "The caller transaction must have an active connection.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "The caller transaction connection must be open.");
        }

        dbContext.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await dbContext.Database.UseTransactionAsync(transaction, cancellationToken);
        if (!ReferenceEquals(
                dbContext.Database.CurrentTransaction?.GetDbTransaction(),
                transaction))
        {
            throw new InvalidOperationException(
                "OperationalConfiguration must use the caller PostgreSQL transaction.");
        }

        var ids = responsibilityIds.Distinct().ToArray();
        return await dbContext.PreparationResponsibilities.AsNoTracking()
            .Where(responsibility => ids.Contains(responsibility.Id))
            .Select(responsibility => new PreparationResponsibilityReference(
                responsibility.Id,
                responsibility.OperationalName))
            .ToArrayAsync(cancellationToken);
    }
}
