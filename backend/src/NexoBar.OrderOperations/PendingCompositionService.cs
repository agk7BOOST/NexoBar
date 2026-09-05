using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class PendingCompositionService(
    OrderOperationsDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOrderOperationsCapabilityStabilizer capabilityStabilizer,
    IOrderOperationsAuthorization authorization,
    TimeProvider timeProvider)
{
    private const long CommandLockNamespace = 0x50434F4D434D4400;

    internal async Task<PendingCompositionCommandResult> StartAsync(
        Guid idempotencyKey,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await AcquireCommandLockAsync(idempotencyKey, cancellationToken);

        var session = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (session is null)
        {
            return PendingCompositionCommandResult.Unauthenticated();
        }

        var existingCommand = await dbContext.PendingCompositionCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existingCommand.MatchesStart(session.IdentityId, orderId)
                ? PendingCompositionCommandResult.Started(existingCommand.ToResponse())
                : PendingCompositionCommandResult.IdempotencyConflict();
        }

        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(
                session.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            return PendingCompositionCommandResult.Forbidden();
        }

        if (!await LockOrderAsync(orderId, cancellationToken))
        {
            return PendingCompositionCommandResult.OrderNotFound();
        }

        if (await IsFrozenAsync(orderId, cancellationToken))
        {
            return PendingCompositionCommandResult.OrderFrozen();
        }

        if (await dbContext.PendingCompositions.AnyAsync(
                pending => pending.OrderId == orderId,
                cancellationToken))
        {
            return PendingCompositionCommandResult.AlreadyExists();
        }

        var createdAt = TruncateToMicroseconds(timeProvider.GetUtcNow());
        var pendingComposition = new PendingComposition(
            Guid.CreateVersion7(createdAt),
            orderId,
            createdAt,
            session.IdentityId);
        dbContext.PendingCompositions.Add(pendingComposition);
        dbContext.PendingCompositionCommands.Add(PendingCompositionCommand.Start(
            idempotencyKey,
            session.IdentityId,
            pendingComposition));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PendingCompositionCommandResult.Started(
            new PendingCompositionResponse(
                pendingComposition.Id,
                pendingComposition.CreatedAt,
                pendingComposition.CreatedByIdentityId));
    }

    internal async Task<CurrentPendingCompositionResult> FindAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var authorizationOutcome = await authorization.AuthorizeAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (authorizationOutcome == OrderOperationsAuthorizationOutcome.Unauthenticated)
        {
            return CurrentPendingCompositionResult.Unauthenticated();
        }

        if (authorizationOutcome == OrderOperationsAuthorizationOutcome.Forbidden)
        {
            return CurrentPendingCompositionResult.Forbidden();
        }

        if (!await dbContext.Orders.AsNoTracking().AnyAsync(
                order => order.Id == orderId,
                cancellationToken))
        {
            return CurrentPendingCompositionResult.OrderNotFound();
        }

        var pending = await dbContext.PendingCompositions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.OrderId == orderId,
                cancellationToken);
        var response = new CurrentPendingCompositionResponse(
            orderId,
            pending is null
                ? null
                : new PendingCompositionResponse(
                    pending.Id,
                    pending.CreatedAt,
                    pending.CreatedByIdentityId));
        await transaction.CommitAsync(cancellationToken);
        return CurrentPendingCompositionResult.Succeeded(response);
    }

    internal async Task<PendingCompositionCommandResult> DiscardAsync(
        Guid idempotencyKey,
        Guid orderId,
        Guid pendingCompositionId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await AcquireCommandLockAsync(idempotencyKey, cancellationToken);

        var session = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (session is null)
        {
            return PendingCompositionCommandResult.Unauthenticated();
        }

        var existingCommand = await dbContext.PendingCompositionCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existingCommand.MatchesDiscard(
                    session.IdentityId,
                    orderId,
                    pendingCompositionId)
                ? PendingCompositionCommandResult.Discarded()
                : PendingCompositionCommandResult.IdempotencyConflict();
        }

        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(
                session.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            return PendingCompositionCommandResult.Forbidden();
        }

        if (!await LockOrderAsync(orderId, cancellationToken))
        {
            return PendingCompositionCommandResult.OrderNotFound();
        }

        if (await IsFrozenAsync(orderId, cancellationToken))
        {
            return PendingCompositionCommandResult.OrderFrozen();
        }

        var pending = await dbContext.PendingCompositions.SingleOrDefaultAsync(
            candidate => candidate.OrderId == orderId,
            cancellationToken);
        if (pending is null || pending.Id != pendingCompositionId)
        {
            return PendingCompositionCommandResult.Stale();
        }

        dbContext.PendingCompositions.Remove(pending);
        dbContext.PendingCompositionCommands.Add(PendingCompositionCommand.Discard(
            idempotencyKey,
            session.IdentityId,
            pending));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PendingCompositionCommandResult.Discarded();
    }

    private async Task AcquireCommandLockAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var lockKey = CreateTransactionLockKey(idempotencyKey);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);
    }

    private async Task<bool> LockOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken) =>
        await dbContext.Orders
            .FromSqlInterpolated(
                $"SELECT id, context FROM order_operations.orders WHERE id = {orderId} FOR UPDATE")
            .AsNoTracking()
            .AnyAsync(cancellationToken);

    private Task<bool> IsFrozenAsync(
        Guid orderId,
        CancellationToken cancellationToken) =>
        dbContext.Liquidations.AsNoTracking().AnyAsync(
            liquidation => liquidation.OrderId == orderId,
            cancellationToken);

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(
            value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);

    private static long CreateTransactionLockKey(Guid idempotencyKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);
        return CommandLockNamespace ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}

internal sealed record PendingCompositionCommandResult(
    PendingCompositionCommandOutcome Outcome,
    PendingCompositionResponse? Response)
{
    internal static PendingCompositionCommandResult Started(
        PendingCompositionResponse response) =>
        new(PendingCompositionCommandOutcome.Started, response);

    internal static PendingCompositionCommandResult Discarded() =>
        new(PendingCompositionCommandOutcome.Discarded, null);

    internal static PendingCompositionCommandResult Unauthenticated() =>
        new(PendingCompositionCommandOutcome.Unauthenticated, null);

    internal static PendingCompositionCommandResult Forbidden() =>
        new(PendingCompositionCommandOutcome.Forbidden, null);

    internal static PendingCompositionCommandResult OrderNotFound() =>
        new(PendingCompositionCommandOutcome.OrderNotFound, null);

    internal static PendingCompositionCommandResult AlreadyExists() =>
        new(PendingCompositionCommandOutcome.AlreadyExists, null);

    internal static PendingCompositionCommandResult Stale() =>
        new(PendingCompositionCommandOutcome.Stale, null);

    internal static PendingCompositionCommandResult IdempotencyConflict() =>
        new(PendingCompositionCommandOutcome.IdempotencyConflict, null);

    internal static PendingCompositionCommandResult OrderFrozen() =>
        new(PendingCompositionCommandOutcome.OrderFrozen, null);
}

internal enum PendingCompositionCommandOutcome
{
    Started,
    Discarded,
    Unauthenticated,
    Forbidden,
    OrderNotFound,
    AlreadyExists,
    Stale,
    IdempotencyConflict,
    OrderFrozen
}

internal sealed record CurrentPendingCompositionResult(
    CurrentPendingCompositionOutcome Outcome,
    CurrentPendingCompositionResponse? Response)
{
    internal static CurrentPendingCompositionResult Succeeded(
        CurrentPendingCompositionResponse response) =>
        new(CurrentPendingCompositionOutcome.Succeeded, response);

    internal static CurrentPendingCompositionResult Unauthenticated() =>
        new(CurrentPendingCompositionOutcome.Unauthenticated, null);

    internal static CurrentPendingCompositionResult Forbidden() =>
        new(CurrentPendingCompositionOutcome.Forbidden, null);

    internal static CurrentPendingCompositionResult OrderNotFound() =>
        new(CurrentPendingCompositionOutcome.OrderNotFound, null);
}

internal enum CurrentPendingCompositionOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden,
    OrderNotFound
}
