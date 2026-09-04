using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory;

internal sealed class InventoryCountService(
    InventoryDbContext dbContext,
    IInventoryAuthorization authorization,
    TimeProvider timeProvider,
    ILogger<InventoryCountService> logger)
{
    private const long CountCommandLockNamespace = 0x494E56434F554E54;
    private const long MovementCommandLockNamespace = 0x494E564D4F56454D;

    internal async Task<RecordInventoryCountResult> RecordAsync(
        Guid idempotencyKey,
        Guid inventoryItemId,
        decimal observedQuantity,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockCommandAsync(
            CountCommandLockNamespace,
            idempotencyKey,
            cancellationToken);

        var actor = await authorization.StabilizeSessionAndIdentityAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (actor is null)
        {
            return RecordInventoryCountResult.Unauthenticated();
        }

        var existing = await dbContext.InventoryCountCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing.Matches(
                    actor.IdentityId,
                    inventoryItemId,
                    observedQuantity)
                ? RecordInventoryCountResult.Recorded(existing.ToResponse())
                : RecordInventoryCountResult.IdempotencyConflict();
        }

        if (!await authorization.StabilizeInventoryOperationAsync(
                actor.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            return RecordInventoryCountResult.Forbidden();
        }

        var item = await dbContext.InventoryItems
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM inventory.inventory_items
                WHERE id = {inventoryItemId}
                FOR SHARE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return RecordInventoryCountResult.ItemNotFound();
        }

        var observedAt = UtcNow();
        var observationId = Guid.CreateVersion7(observedAt);
        var response = new CountObservationResponse(
            observationId,
            item.Id,
            InventoryQuantity.Format(observedQuantity),
            item.MovementRevision,
            item.OperationalUnit.Value,
            observedAt);
        dbContext.CountObservations.Add(new CountObservation(
            observationId,
            item.Id,
            observedQuantity,
            item.MovementRevision,
            item.OperationalUnit.Value,
            observedAt,
            actor.IdentityId));
        dbContext.InventoryCountCommands.Add(new InventoryCountCommand(
            idempotencyKey,
            actor.IdentityId,
            item.Id,
            observedQuantity,
            response));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RecordInventoryCountResult.Recorded(response);
    }

    internal async Task<ReconcileInventoryCountResult> ReconcileAsync(
        Guid idempotencyKey,
        Guid inventoryItemId,
        Guid countObservationId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockCommandAsync(
            MovementCommandLockNamespace,
            idempotencyKey,
            cancellationToken);

        var actor = await authorization.StabilizeSessionAndIdentityAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (actor is null)
        {
            return ReconcileInventoryCountResult.Unauthenticated();
        }

        var existing = await dbContext.InventoryMovementCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing.Matches(
                    actor.IdentityId,
                    inventoryItemId,
                    countObservationId)
                ? ReconcileInventoryCountResult.Succeeded(existing.ToResponse())
                : ReconcileInventoryCountResult.IdempotencyConflict();
        }

        if (!await authorization.StabilizeInventoryOperationAsync(
                actor.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            return ReconcileInventoryCountResult.Forbidden();
        }

        var item = await dbContext.InventoryItems
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM inventory.inventory_items
                WHERE id = {inventoryItemId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return ReconcileInventoryCountResult.ItemNotFound();
        }

        var observation = await dbContext.CountObservations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == countObservationId &&
                    candidate.InventoryItemId == inventoryItemId,
                cancellationToken);
        if (observation is null)
        {
            return ReconcileInventoryCountResult.ObservationNotFound();
        }

        if (observation.ObservedMovementRevision != item.MovementRevision)
        {
            return ReconcileInventoryCountResult.CountInvalidated();
        }

        if (!string.Equals(
                observation.ObservedOperationalUnit,
                item.OperationalUnit.Value,
                StringComparison.Ordinal))
        {
            return ReconcileInventoryCountResult.ConfigurationChanged();
        }

        InventoryReconciliationTransition transition;
        try
        {
            transition = item.Reconcile(observation.ObservedQuantity);
        }
        catch (OverflowException exception)
        {
            logger.LogError(
                exception,
                "Movement revision overflow for Inventory Item {InventoryItemId}.",
                inventoryItemId);
            return ReconcileInventoryCountResult.RevisionOverflow();
        }

        Guid? movementId = null;
        DateTimeOffset? occurredAt = null;
        if (transition.MovementRequired)
        {
            occurredAt = UtcNow();
            movementId = Guid.CreateVersion7(occurredAt.Value);
            dbContext.InventoryMovements.Add(InventoryMovement.Reconciliation(
                movementId.Value,
                item.Id,
                transition.MovementRevision,
                observation.ObservedQuantity,
                transition.PreviousRegisteredQuantity,
                transition.ResultingRegisteredQuantity,
                observation.Id,
                occurredAt.Value,
                actor.IdentityId));
        }

        var response = new ReconcileInventoryCountResponse(
            item.Id,
            observation.Id,
            transition.MovementRequired ? "reconciled" : "no_discrepancy",
            movementId,
            occurredAt,
            InventoryQuantity.Format(transition.PreviousRegisteredQuantity),
            InventoryQuantity.Format(transition.ObservedQuantity),
            InventoryQuantity.Format(transition.Difference),
            InventoryQuantity.Format(transition.ResultingRegisteredQuantity),
            transition.MovementRevision);
        dbContext.InventoryMovementCommands.Add(new InventoryMovementCommand(
            idempotencyKey,
            actor.IdentityId,
            item.Id,
            observation.Id,
            response));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReconcileInventoryCountResult.Succeeded(response);
    }

    private async Task LockCommandAsync(
        long lockNamespace,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var lockKey = CreateTransactionLockKey(lockNamespace, idempotencyKey);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);
    }

    private DateTimeOffset UtcNow()
    {
        var now = timeProvider.GetUtcNow();
        return new DateTimeOffset(
            now.Ticks - (now.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);
    }

    private static long CreateTransactionLockKey(
        long lockNamespace,
        Guid idempotencyKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);
        return lockNamespace ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}

internal sealed record RecordInventoryCountResult(
    RecordInventoryCountOutcome Outcome,
    CountObservationResponse? Response)
{
    internal static RecordInventoryCountResult Recorded(CountObservationResponse response) =>
        new(RecordInventoryCountOutcome.Recorded, response);
    internal static RecordInventoryCountResult Unauthenticated() =>
        new(RecordInventoryCountOutcome.Unauthenticated, null);
    internal static RecordInventoryCountResult Forbidden() =>
        new(RecordInventoryCountOutcome.Forbidden, null);
    internal static RecordInventoryCountResult ItemNotFound() =>
        new(RecordInventoryCountOutcome.ItemNotFound, null);
    internal static RecordInventoryCountResult IdempotencyConflict() =>
        new(RecordInventoryCountOutcome.IdempotencyConflict, null);
}

internal enum RecordInventoryCountOutcome
{
    Recorded,
    Unauthenticated,
    Forbidden,
    ItemNotFound,
    IdempotencyConflict
}

internal sealed record ReconcileInventoryCountResult(
    ReconcileInventoryCountOutcome Outcome,
    ReconcileInventoryCountResponse? Response)
{
    internal static ReconcileInventoryCountResult Succeeded(
        ReconcileInventoryCountResponse response) =>
        new(ReconcileInventoryCountOutcome.Succeeded, response);
    internal static ReconcileInventoryCountResult Unauthenticated() =>
        new(ReconcileInventoryCountOutcome.Unauthenticated, null);
    internal static ReconcileInventoryCountResult Forbidden() =>
        new(ReconcileInventoryCountOutcome.Forbidden, null);
    internal static ReconcileInventoryCountResult ItemNotFound() =>
        new(ReconcileInventoryCountOutcome.ItemNotFound, null);
    internal static ReconcileInventoryCountResult ObservationNotFound() =>
        new(ReconcileInventoryCountOutcome.ObservationNotFound, null);
    internal static ReconcileInventoryCountResult CountInvalidated() =>
        new(ReconcileInventoryCountOutcome.CountInvalidated, null);
    internal static ReconcileInventoryCountResult ConfigurationChanged() =>
        new(ReconcileInventoryCountOutcome.ConfigurationChanged, null);
    internal static ReconcileInventoryCountResult IdempotencyConflict() =>
        new(ReconcileInventoryCountOutcome.IdempotencyConflict, null);
    internal static ReconcileInventoryCountResult RevisionOverflow() =>
        new(ReconcileInventoryCountOutcome.RevisionOverflow, null);
}

internal enum ReconcileInventoryCountOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden,
    ItemNotFound,
    ObservationNotFound,
    CountInvalidated,
    ConfigurationChanged,
    IdempotencyConflict,
    RevisionOverflow
}
