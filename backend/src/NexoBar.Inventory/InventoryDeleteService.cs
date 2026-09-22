using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory;

internal sealed class InventoryDeleteService(
    InventoryDbContext dbContext,
    IInventoryAuthorization authorization,
    TimeProvider timeProvider,
    IInventoryOperationInvalidationPublisher invalidations)
{
    private const long DeleteCommandLockNamespace = 0x494E5644454C4554;

    internal async Task<InventoryDeleteCommandResult> DeleteAsync(
        Guid idempotencyKey,
        Guid inventoryItemId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(
            idempotencyKey,
            cancellationToken);
        var dbTransaction = transaction.GetDbTransaction();
        var actor = await authorization.StabilizeSessionAndIdentityAsync(
            dbTransaction,
            cancellationToken);
        if (actor is null)
        {
            return InventoryDeleteCommandResult.Unauthenticated();
        }

        var existing = await dbContext.InventoryDeleteCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing.Matches(actor.IdentityId, inventoryItemId)
                ? InventoryDeleteCommandResult.Deleted(existing.ToResponse())
                : InventoryDeleteCommandResult.IdempotencyConflict();
        }

        if (!await authorization.StabilizeInventoryConfigurationAsync(
                actor.IdentityId,
                dbTransaction,
                cancellationToken))
        {
            return InventoryDeleteCommandResult.Forbidden();
        }

        var item = await LockItemAsync(inventoryItemId, cancellationToken);
        if (item is null)
        {
            return InventoryDeleteCommandResult.NotFound();
        }

        if (await dbContext.InventoryMovements.AnyAsync(
                movement => movement.InventoryItemId == inventoryItemId,
                cancellationToken))
        {
            return InventoryDeleteCommandResult.MovementHistoryConflict();
        }

        await dbContext.CountObservations
            .Where(observation => observation.InventoryItemId == inventoryItemId)
            .ExecuteDeleteAsync(cancellationToken);

        var response = new InventoryItemDeleteResponse(item.Id, true);
        dbContext.InventoryDeleteCommands.Add(new InventoryDeleteCommand(
            idempotencyKey,
            actor.IdentityId,
            item.Id,
            response,
            UtcNow()));
        dbContext.InventoryItems.Remove(item);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        invalidations.PublishChanged();
        return InventoryDeleteCommandResult.Deleted(response);
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

    private async Task<IDbContextTransaction> BeginTransactionAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var lockKey = CreateTransactionLockKey(idempotencyKey);
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

    private static long CreateTransactionLockKey(Guid idempotencyKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);
        return DeleteCommandLockNamespace ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}

internal sealed record InventoryDeleteCommandResult(
    InventoryDeleteCommandOutcome Outcome,
    InventoryItemDeleteResponse? Response)
{
    internal static InventoryDeleteCommandResult Deleted(
        InventoryItemDeleteResponse response) =>
        new(InventoryDeleteCommandOutcome.Deleted, response);

    internal static InventoryDeleteCommandResult Unauthenticated() =>
        new(InventoryDeleteCommandOutcome.Unauthenticated, null);

    internal static InventoryDeleteCommandResult Forbidden() =>
        new(InventoryDeleteCommandOutcome.Forbidden, null);

    internal static InventoryDeleteCommandResult NotFound() =>
        new(InventoryDeleteCommandOutcome.NotFound, null);

    internal static InventoryDeleteCommandResult MovementHistoryConflict() =>
        new(InventoryDeleteCommandOutcome.MovementHistoryConflict, null);

    internal static InventoryDeleteCommandResult IdempotencyConflict() =>
        new(InventoryDeleteCommandOutcome.IdempotencyConflict, null);
}

internal enum InventoryDeleteCommandOutcome
{
    Deleted,
    Unauthenticated,
    Forbidden,
    NotFound,
    MovementHistoryConflict,
    IdempotencyConflict
}
