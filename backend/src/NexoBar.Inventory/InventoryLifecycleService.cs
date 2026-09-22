using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.Inventory;

internal sealed class InventoryLifecycleService(
    InventoryDbContext dbContext,
    IInventoryAuthorization authorization,
    TimeProvider timeProvider,
    IInventoryOperationInvalidationPublisher invalidations)
{
    private const long RetireCommandLockNamespace = 0x494E565245544952;
    private const long ReactivateCommandLockNamespace = 0x494E565245414354;
    private const long UnitCorrectionCommandLockNamespace = 0x494E56554E495443;

    internal async Task<InventoryLifecycleCommandResult> RetireAsync(
        Guid idempotencyKey,
        Guid inventoryItemId,
        bool expectedCurrentIsActive,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(
            RetireCommandLockNamespace,
            idempotencyKey,
            cancellationToken);
        var dbTransaction = transaction.GetDbTransaction();
        var actor = await authorization.StabilizeSessionAndIdentityAsync(
            dbTransaction,
            cancellationToken);
        if (actor is null)
        {
            return InventoryLifecycleCommandResult.Unauthenticated();
        }

        var existing = await dbContext.InventoryRetireCommands
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
                    expectedCurrentIsActive)
                ? InventoryLifecycleCommandResult.Changed(existing.ToResponse())
                : InventoryLifecycleCommandResult.IdempotencyConflict();
        }

        if (!await authorization.StabilizeInventoryConfigurationAsync(
                actor.IdentityId,
                dbTransaction,
                cancellationToken))
        {
            return InventoryLifecycleCommandResult.Forbidden();
        }

        var item = await LockItemAsync(inventoryItemId, cancellationToken);
        if (item is null)
        {
            return InventoryLifecycleCommandResult.NotFound();
        }

        if (!item.IsActive)
        {
            return InventoryLifecycleCommandResult.AlreadyRetired();
        }

        if (!expectedCurrentIsActive)
        {
            return InventoryLifecycleCommandResult.LifecycleConcurrencyConflict(
                item.IsActive);
        }

        item.Retire();
        await InvalidatePendingCountsAsync(item.Id, cancellationToken);
        var response = MapLifecycle(item);
        dbContext.InventoryRetireCommands.Add(new InventoryRetireCommand(
            idempotencyKey,
            actor.IdentityId,
            item.Id,
            expectedCurrentIsActive,
            response));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        invalidations.PublishChanged();
        return InventoryLifecycleCommandResult.Changed(response);
    }

    internal async Task<InventoryLifecycleCommandResult> ReactivateAsync(
        Guid idempotencyKey,
        Guid inventoryItemId,
        bool expectedCurrentIsActive,
        string? rawNewOperationalName,
        CancellationToken cancellationToken)
    {
        var nameResult = NormalizeOptionalName(rawNewOperationalName);
        if (nameResult.Error is not null)
        {
            return InventoryLifecycleCommandResult.InvalidName();
        }

        await using var transaction = await BeginTransactionAsync(
            ReactivateCommandLockNamespace,
            idempotencyKey,
            cancellationToken);
        var dbTransaction = transaction.GetDbTransaction();
        var actor = await authorization.StabilizeSessionAndIdentityAsync(
            dbTransaction,
            cancellationToken);
        if (actor is null)
        {
            return InventoryLifecycleCommandResult.Unauthenticated();
        }

        var existing = await dbContext.InventoryReactivateCommands
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
                    expectedCurrentIsActive,
                    nameResult.Name)
                ? InventoryLifecycleCommandResult.Changed(existing.ToResponse())
                : InventoryLifecycleCommandResult.IdempotencyConflict();
        }

        if (!await authorization.StabilizeInventoryConfigurationAsync(
                actor.IdentityId,
                dbTransaction,
                cancellationToken))
        {
            return InventoryLifecycleCommandResult.Forbidden();
        }

        var item = await LockItemAsync(inventoryItemId, cancellationToken);
        if (item is null)
        {
            return InventoryLifecycleCommandResult.NotFound();
        }

        if (item.IsActive)
        {
            return InventoryLifecycleCommandResult.AlreadyActive();
        }

        if (expectedCurrentIsActive)
        {
            return InventoryLifecycleCommandResult.LifecycleConcurrencyConflict(
                item.IsActive);
        }

        var operationalName = nameResult.Name ?? item.OperationalName;
        item.Reactivate(operationalName);
        await InvalidatePendingCountsAsync(item.Id, cancellationToken);
        var response = MapLifecycle(item);
        dbContext.InventoryReactivateCommands.Add(new InventoryReactivateCommand(
            idempotencyKey,
            actor.IdentityId,
            item.Id,
            expectedCurrentIsActive,
            nameResult.Name,
            response));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (IsUniqueViolation(
                exception,
                "UX_inventory_items_active_normalized_name"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return InventoryLifecycleCommandResult.NameConflict();
        }

        invalidations.PublishChanged();
        return InventoryLifecycleCommandResult.Changed(response);
    }

    internal async Task<InventoryUnitCorrectionResult> CorrectUnitAsync(
        Guid idempotencyKey,
        Guid inventoryItemId,
        string? rawExpectedCurrentUnit,
        string? rawNewUnit,
        CancellationToken cancellationToken)
    {
        if (!OperationalUnit.TryCreate(
                rawExpectedCurrentUnit,
                out var expectedCurrentUnit) ||
            !OperationalUnit.TryCreate(rawNewUnit, out var newUnit))
        {
            return InventoryUnitCorrectionResult.InvalidUnit();
        }

        await using var transaction = await BeginTransactionAsync(
            UnitCorrectionCommandLockNamespace,
            idempotencyKey,
            cancellationToken);
        var dbTransaction = transaction.GetDbTransaction();
        var actor = await authorization.StabilizeSessionAndIdentityAsync(
            dbTransaction,
            cancellationToken);
        if (actor is null)
        {
            return InventoryUnitCorrectionResult.Unauthenticated();
        }

        var existing = await dbContext.InventoryUnitCorrectionCommands
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
                    expectedCurrentUnit.Value,
                    newUnit.Value)
                ? InventoryUnitCorrectionResult.Succeeded(existing.ToResponse())
                : InventoryUnitCorrectionResult.IdempotencyConflict();
        }

        if (!await authorization.StabilizeInventoryConfigurationAsync(
                actor.IdentityId,
                dbTransaction,
                cancellationToken))
        {
            return InventoryUnitCorrectionResult.Forbidden();
        }

        var item = await LockItemAsync(inventoryItemId, cancellationToken);
        if (item is null)
        {
            return InventoryUnitCorrectionResult.NotFound();
        }

        if (!string.Equals(
                item.OperationalUnit.Value,
                expectedCurrentUnit.Value,
                StringComparison.Ordinal))
        {
            return InventoryUnitCorrectionResult.UnitConcurrencyConflict(
                item.OperationalUnit.Value);
        }

        if (await dbContext.InventoryMovements.AnyAsync(
                movement => movement.InventoryItemId == inventoryItemId,
                cancellationToken))
        {
            return InventoryUnitCorrectionResult.HistoryExists();
        }

        var changed = !string.Equals(
            expectedCurrentUnit.Value,
            newUnit.Value,
            StringComparison.Ordinal);
        var outcome = changed ? "corrected" : "no_change";
        if (changed)
        {
            item.CorrectOperationalUnit(newUnit);
            await InvalidatePendingCountsAsync(item.Id, cancellationToken);
        }

        var response = new InventoryUnitCorrectionResponse(
            item.Id,
            item.OperationalUnit.Value,
            outcome);
        dbContext.InventoryUnitCorrectionCommands.Add(
            new InventoryUnitCorrectionCommand(
                idempotencyKey,
                actor.IdentityId,
                item.Id,
                expectedCurrentUnit.Value,
                newUnit.Value,
                response));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (changed)
        {
            invalidations.PublishChanged();
        }

        return InventoryUnitCorrectionResult.Succeeded(response);
    }

    private async Task<InventoryItem?> LockItemAsync(
        Guid inventoryItemId,
        CancellationToken cancellationToken) =>
        await dbContext.InventoryItems
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM inventory.inventory_items
                WHERE id = {inventoryItemId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

    private Task InvalidatePendingCountsAsync(
        Guid inventoryItemId,
        CancellationToken cancellationToken) =>
        InventoryCountInvalidation.InvalidatePendingAsync(
            dbContext,
            inventoryItemId,
            UtcNow(),
            cancellationToken);

    private async Task<IDbContextTransaction> BeginTransactionAsync(
        long lockNamespace,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var lockKey = CreateTransactionLockKey(lockNamespace, idempotencyKey);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);
        return transaction;
    }

    private DateTimeOffset UtcNow()
    {
        var now = timeProvider.GetUtcNow();
        return new DateTimeOffset(
            now.Ticks - (now.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);
    }

    private static InventoryLifecycleResponse MapLifecycle(InventoryItem item) =>
        new(
            item.Id,
            item.OperationalName,
            item.OperationalUnit.Value,
            item.IsActive,
            item.IsActive && item.CurrentRegisteredQuantity is not null,
            item.IsActive && item.CurrentRegisteredQuantity is null,
            item.MovementRevision);

    private static (string? Name, InventoryItemValidationError? Error)
        NormalizeOptionalName(string? rawName)
    {
        if (rawName is null)
        {
            return (null, null);
        }

        return InventoryItem.TryNormalizeOperationalName(
            rawName,
            out var operationalName,
            out _)
            ? (operationalName, null)
            : (null, InventoryItemValidationError.OperationalNameInvalid);
    }

    private static bool IsUniqueViolation(
        DbUpdateException exception,
        string constraintName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: var actualConstraint
        } && string.Equals(actualConstraint, constraintName, StringComparison.Ordinal);

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

internal sealed record InventoryLifecycleCommandResult(
    InventoryLifecycleCommandOutcome Outcome,
    InventoryLifecycleResponse? Response,
    bool? CurrentIsActive = null)
{
    internal static InventoryLifecycleCommandResult Changed(
        InventoryLifecycleResponse response) =>
        new(InventoryLifecycleCommandOutcome.Changed, response);

    internal static InventoryLifecycleCommandResult Unauthenticated() =>
        new(InventoryLifecycleCommandOutcome.Unauthenticated, null);

    internal static InventoryLifecycleCommandResult Forbidden() =>
        new(InventoryLifecycleCommandOutcome.Forbidden, null);

    internal static InventoryLifecycleCommandResult NotFound() =>
        new(InventoryLifecycleCommandOutcome.NotFound, null);

    internal static InventoryLifecycleCommandResult AlreadyRetired() =>
        new(InventoryLifecycleCommandOutcome.AlreadyRetired, null);

    internal static InventoryLifecycleCommandResult AlreadyActive() =>
        new(InventoryLifecycleCommandOutcome.AlreadyActive, null);

    internal static InventoryLifecycleCommandResult LifecycleConcurrencyConflict(
        bool currentIsActive) =>
        new(
            InventoryLifecycleCommandOutcome.LifecycleConcurrencyConflict,
            null,
            currentIsActive);

    internal static InventoryLifecycleCommandResult InvalidName() =>
        new(InventoryLifecycleCommandOutcome.InvalidName, null);

    internal static InventoryLifecycleCommandResult NameConflict() =>
        new(InventoryLifecycleCommandOutcome.NameConflict, null);

    internal static InventoryLifecycleCommandResult IdempotencyConflict() =>
        new(InventoryLifecycleCommandOutcome.IdempotencyConflict, null);
}

internal enum InventoryLifecycleCommandOutcome
{
    Changed,
    Unauthenticated,
    Forbidden,
    NotFound,
    AlreadyRetired,
    AlreadyActive,
    LifecycleConcurrencyConflict,
    InvalidName,
    NameConflict,
    IdempotencyConflict
}

internal sealed record InventoryUnitCorrectionResult(
    InventoryUnitCorrectionOutcome Outcome,
    InventoryUnitCorrectionResponse? Response,
    string? CurrentUnit = null)
{
    internal static InventoryUnitCorrectionResult Succeeded(
        InventoryUnitCorrectionResponse response) =>
        new(InventoryUnitCorrectionOutcome.Succeeded, response);

    internal static InventoryUnitCorrectionResult Unauthenticated() =>
        new(InventoryUnitCorrectionOutcome.Unauthenticated, null);

    internal static InventoryUnitCorrectionResult Forbidden() =>
        new(InventoryUnitCorrectionOutcome.Forbidden, null);

    internal static InventoryUnitCorrectionResult NotFound() =>
        new(InventoryUnitCorrectionOutcome.NotFound, null);

    internal static InventoryUnitCorrectionResult InvalidUnit() =>
        new(InventoryUnitCorrectionOutcome.InvalidUnit, null);

    internal static InventoryUnitCorrectionResult UnitConcurrencyConflict(
        string currentUnit) =>
        new(
            InventoryUnitCorrectionOutcome.UnitConcurrencyConflict,
            null,
            currentUnit);

    internal static InventoryUnitCorrectionResult HistoryExists() =>
        new(InventoryUnitCorrectionOutcome.HistoryExists, null);

    internal static InventoryUnitCorrectionResult IdempotencyConflict() =>
        new(InventoryUnitCorrectionOutcome.IdempotencyConflict, null);
}

internal enum InventoryUnitCorrectionOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden,
    NotFound,
    InvalidUnit,
    UnitConcurrencyConflict,
    HistoryExists,
    IdempotencyConflict
}
