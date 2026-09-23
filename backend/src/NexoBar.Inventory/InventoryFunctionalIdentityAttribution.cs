using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory;

internal sealed class InventoryFunctionalIdentityAttribution(
    InventoryDbContext db, TimeProvider timeProvider)
    : IInventoryFunctionalIdentityAttribution
{
    public async Task<bool> HasFunctionalAttributionAsync(
        Guid identityId, DbTransaction transaction, CancellationToken cancellationToken)
    {
        db.Database.SetDbConnection(transaction.Connection ?? throw new InvalidOperationException(
            "Identity Delete requires an open PostgreSQL transaction."), contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        if (!ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), transaction))
            throw new InvalidOperationException("Inventory must share the Identity Delete transaction.");

        // A different actor can reconcile this Identity's pending Count. Lock the
        // observation before checking its use, then hold the lock through Delete.
        await db.CountObservations.FromSqlInterpolated(
            $"SELECT * FROM inventory.count_observations WHERE actor_identity_id = {identityId} FOR UPDATE")
            .AsNoTracking().ToArrayAsync(cancellationToken);

        return await db.InventoryMovements.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken) ||
            await db.InventoryMovementCorrections.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken) ||
            await (from countCommand in db.InventoryCountCommands.AsNoTracking()
                   join reconciliation in db.InventoryMovementCommands.AsNoTracking()
                       on countCommand.ResultCountObservationId equals reconciliation.CountObservationId
                   where countCommand.ActorIdentityId == identityId &&
                       reconciliation.CommandKind == InventoryMovementCommand.ReconcileCountCommandKind
                   select countCommand.IdempotencyKey).AnyAsync(cancellationToken) ||
            await (from count in db.CountObservations.AsNoTracking()
                   join reconciliation in db.InventoryMovementCommands.AsNoTracking()
                       on count.Id equals reconciliation.CountObservationId
                   where count.ActorIdentityId == identityId &&
                       reconciliation.CommandKind == InventoryMovementCommand.ReconcileCountCommandKind
                   select count.Id).AnyAsync(cancellationToken);
    }

    public async Task InvalidateUnusedCountsAsync(
        Guid identityId, DbTransaction transaction, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), transaction))
            throw new InvalidOperationException("Inventory must share the Identity Delete transaction.");
        var now = timeProvider.GetUtcNow();
        await db.CountObservations
            .Where(x => x.ActorIdentityId == identityId && x.InvalidatedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.InvalidatedAtUtc, now),
                cancellationToken);
    }
}
