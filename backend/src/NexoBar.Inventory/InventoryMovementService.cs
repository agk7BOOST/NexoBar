using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory;

internal sealed class InventoryMovementService(
    InventoryDbContext dbContext,
    IInventoryAuthorization authorization,
    TimeProvider timeProvider,
    ILogger<InventoryMovementService> logger)
{
    private const long MovementCommandLockNamespace = 0x494E564D4F56454D;

    internal Task<RecordInventoryMovementResult> RecordEntryAsync(
        Guid idempotencyKey,
        Guid inventoryItemId,
        decimal quantity,
        CancellationToken cancellationToken) =>
        RecordAsync(
            idempotencyKey,
            inventoryItemId,
            InventoryMovementCommand.RecordEntryCommandKind,
            quantity,
            cancellationToken);

    internal Task<RecordInventoryMovementResult> RecordManualExitAsync(
        Guid idempotencyKey,
        Guid inventoryItemId,
        decimal quantity,
        CancellationToken cancellationToken) =>
        RecordAsync(
            idempotencyKey,
            inventoryItemId,
            InventoryMovementCommand.RecordManualExitCommandKind,
            quantity,
            cancellationToken);

    internal Task<RecordInventoryMovementResult> RecordWasteAsync(
        Guid idempotencyKey,
        Guid inventoryItemId,
        decimal quantity,
        CancellationToken cancellationToken) =>
        RecordAsync(
            idempotencyKey,
            inventoryItemId,
            InventoryMovementCommand.RecordWasteCommandKind,
            quantity,
            cancellationToken);

    private async Task<RecordInventoryMovementResult> RecordAsync(
        Guid idempotencyKey,
        Guid inventoryItemId,
        string commandKind,
        decimal quantity,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockCommandAsync(idempotencyKey, cancellationToken);

        var actor = await authorization.StabilizeSessionAndIdentityAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (actor is null)
        {
            return RecordInventoryMovementResult.Unauthenticated();
        }

        var existing = await dbContext.InventoryMovementCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing.MatchesQuantityMovement(
                    actor.IdentityId,
                    inventoryItemId,
                    commandKind,
                    quantity)
                ? RecordInventoryMovementResult.Succeeded(
                    existing.ToQuantityMovementResponse())
                : RecordInventoryMovementResult.IdempotencyConflict();
        }

        if (!await authorization.StabilizeInventoryOperationAsync(
                actor.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            return RecordInventoryMovementResult.Forbidden();
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
            return RecordInventoryMovementResult.ItemNotFound();
        }

        InventoryItemMovementResult transition;
        try
        {
            transition = commandKind switch
            {
                InventoryMovementCommand.RecordEntryCommandKind =>
                    item.RecordEntry(quantity),
                InventoryMovementCommand.RecordManualExitCommandKind =>
                    item.RecordManualExit(quantity),
                InventoryMovementCommand.RecordWasteCommandKind =>
                    item.RecordWaste(quantity),
                _ => throw new InvalidOperationException(
                    $"Unsupported Inventory Movement command '{commandKind}'.")
            };
        }
        catch (OverflowException exception)
        {
            logger.LogError(
                exception,
                "Movement revision overflow for Inventory Item {InventoryItemId}.",
                inventoryItemId);
            return RecordInventoryMovementResult.RevisionOverflow();
        }

        if (transition.Failure == InventoryItemMovementFailure.QuantityNotEstablished)
        {
            return RecordInventoryMovementResult.QuantityNotEstablished();
        }

        if (transition.Failure == InventoryItemMovementFailure.ResultOutOfRange)
        {
            return RecordInventoryMovementResult.ResultOutOfRange();
        }

        var occurredAt = UtcNow();
        var movementId = Guid.CreateVersion7(occurredAt);
        var nature = InventoryMovementCommand.NatureForCommand(commandKind);
        var response = new InventoryMovementResponse(
            movementId,
            item.Id,
            nature,
            InventoryQuantity.Format(quantity),
            InventoryQuantity.Format(transition.PreviousRegisteredQuantity),
            InventoryQuantity.Format(transition.ResultingRegisteredQuantity),
            transition.MovementRevision,
            occurredAt);

        dbContext.InventoryMovements.Add(commandKind switch
        {
            InventoryMovementCommand.RecordEntryCommandKind => InventoryMovement.Entry(
                movementId,
                item.Id,
                transition.MovementRevision,
                quantity,
                transition.PreviousRegisteredQuantity,
                transition.ResultingRegisteredQuantity,
                occurredAt,
                actor.IdentityId),
            InventoryMovementCommand.RecordManualExitCommandKind =>
                InventoryMovement.ManualExit(
                    movementId,
                    item.Id,
                    transition.MovementRevision,
                    quantity,
                    transition.PreviousRegisteredQuantity,
                    transition.ResultingRegisteredQuantity,
                    occurredAt,
                    actor.IdentityId),
            InventoryMovementCommand.RecordWasteCommandKind => InventoryMovement.Waste(
                movementId,
                item.Id,
                transition.MovementRevision,
                quantity,
                transition.PreviousRegisteredQuantity,
                transition.ResultingRegisteredQuantity,
                occurredAt,
                actor.IdentityId),
            _ => throw new InvalidOperationException(
                $"Unsupported Inventory Movement command '{commandKind}'.")
        });
        dbContext.InventoryMovementCommands.Add(new InventoryMovementCommand(
            idempotencyKey,
            actor.IdentityId,
            item.Id,
            commandKind,
            quantity,
            response));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RecordInventoryMovementResult.Succeeded(response);
    }

    private async Task LockCommandAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var lockKey = CreateTransactionLockKey(
            MovementCommandLockNamespace,
            idempotencyKey);
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

internal sealed record RecordInventoryMovementResult(
    RecordInventoryMovementOutcome Outcome,
    InventoryMovementResponse? Response)
{
    internal static RecordInventoryMovementResult Succeeded(
        InventoryMovementResponse response) =>
        new(RecordInventoryMovementOutcome.Succeeded, response);

    internal static RecordInventoryMovementResult Unauthenticated() =>
        new(RecordInventoryMovementOutcome.Unauthenticated, null);

    internal static RecordInventoryMovementResult Forbidden() =>
        new(RecordInventoryMovementOutcome.Forbidden, null);

    internal static RecordInventoryMovementResult ItemNotFound() =>
        new(RecordInventoryMovementOutcome.ItemNotFound, null);

    internal static RecordInventoryMovementResult QuantityNotEstablished() =>
        new(RecordInventoryMovementOutcome.QuantityNotEstablished, null);

    internal static RecordInventoryMovementResult ResultOutOfRange() =>
        new(RecordInventoryMovementOutcome.ResultOutOfRange, null);

    internal static RecordInventoryMovementResult IdempotencyConflict() =>
        new(RecordInventoryMovementOutcome.IdempotencyConflict, null);

    internal static RecordInventoryMovementResult RevisionOverflow() =>
        new(RecordInventoryMovementOutcome.RevisionOverflow, null);
}

internal enum RecordInventoryMovementOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden,
    ItemNotFound,
    QuantityNotEstablished,
    ResultOutOfRange,
    IdempotencyConflict,
    RevisionOverflow
}
