using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NexoBar.OperationalConfiguration;

public interface IPreparationResponsibilityLookup
{
    Task<bool> ExistsAsync(Guid responsibilityId, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid responsibilityId, DbTransaction transaction, CancellationToken cancellationToken);

    Task<bool> IsActiveAsync(Guid responsibilityId, DbTransaction transaction, CancellationToken cancellationToken);

    Task<IReadOnlyList<PreparationResponsibilityReference>> ListActiveAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PreparationResponsibilityReference>> ListAsync(
        CancellationToken cancellationToken);

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
    public async Task<bool> ExistsAsync(Guid responsibilityId, DbTransaction transaction, CancellationToken cancellationToken) =>
        await ReadLockedAsync(responsibilityId, transaction, cancellationToken) is not null;

    public async Task<bool> IsActiveAsync(Guid responsibilityId, DbTransaction transaction, CancellationToken cancellationToken) =>
        await ReadLockedAsync(responsibilityId, transaction, cancellationToken) is { IsActive: true };

    private async Task<PreparationResponsibility?> ReadLockedAsync(Guid id, DbTransaction transaction, CancellationToken token)
    {
        dbContext.Database.SetDbConnection(transaction.Connection!, contextOwnsConnection: false);
        await dbContext.Database.UseTransactionAsync(transaction, token);
        return await dbContext.PreparationResponsibilities.FromSqlInterpolated(
                $"SELECT * FROM operational_configuration.preparation_responsibilities WHERE id = {id} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(token);
    }

    public async Task<IReadOnlyList<PreparationResponsibilityReference>> ListActiveAsync(CancellationToken cancellationToken) =>
        await dbContext.PreparationResponsibilities.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.OperationalName).ThenBy(x => x.Id)
            .Select(x => new PreparationResponsibilityReference(x.Id, x.OperationalName)).ToArrayAsync(cancellationToken);

    public Task<bool> ExistsAsync(
        Guid responsibilityId,
        CancellationToken cancellationToken) =>
        dbContext.PreparationResponsibilities
            .AsNoTracking()
            .AnyAsync(
                responsibility => responsibility.Id == responsibilityId,
                cancellationToken);

    public async Task<IReadOnlyList<PreparationResponsibilityReference>> ListAsync(
        CancellationToken cancellationToken) =>
        await dbContext.PreparationResponsibilities.AsNoTracking()
            .OrderBy(responsibility => responsibility.OperationalName)
            .ThenBy(responsibility => responsibility.Id)
            .Select(responsibility => new PreparationResponsibilityReference(
                responsibility.Id,
                responsibility.OperationalName))
            .ToArrayAsync(cancellationToken);

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
