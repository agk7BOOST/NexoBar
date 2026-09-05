using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class ClosureService(
    OrderOperationsDbContext dbContext,
    ClosureStateReader stateReader,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOrderOperationsCapabilityStabilizer capabilityStabilizer,
    TimeProvider timeProvider)
{
    internal async Task<ClosureResult> CloseAsync(Guid key, Guid orderId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var lockKey = CreateTransactionLockKey(key);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);
        var session = await sessionStabilizer.StabilizeAsync(transaction.GetDbTransaction(), cancellationToken);
        if (session is null)
        {
            return new(ClosureOutcome.Unauthenticated);
        }

        var command = await dbContext.ClosureCommands.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
        if (command is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return command.Matches(session.IdentityId, orderId)
                ? new(ClosureOutcome.Succeeded, command.ToResponse())
                : new(ClosureOutcome.IdempotencyConflict);
        }

        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(
                session.IdentityId, transaction.GetDbTransaction(), cancellationToken))
        {
            return new(ClosureOutcome.Forbidden);
        }

        if (!await dbContext.Orders.FromSqlInterpolated(
                $"SELECT id, context FROM order_operations.orders WHERE id = {orderId} FOR UPDATE")
                .AsNoTracking().AnyAsync(cancellationToken))
        {
            return new(ClosureOutcome.OrderNotFound);
        }

        var state = await stateReader.ReadAsync(orderId, cancellationToken);
        if (state.IsInconsistent)
        {
            return new(ClosureOutcome.StateInconsistent);
        }
        if (state.Closure is not null)
        {
            return new(ClosureOutcome.AlreadyClosed);
        }
        if (state.Liquidation is null)
        {
            return new(ClosureOutcome.NotLiquidated);
        }

        var now = timeProvider.GetUtcNow();
        var closedAt = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        if (closedAt < state.Liquidation.OccurredAt)
        {
            return new(ClosureOutcome.StateInconsistent);
        }
        var closure = new Closure(Guid.CreateVersion7(closedAt), orderId, closedAt, session.IdentityId);
        var resultCommand = new ClosureCommand(key, closure);
        dbContext.Closures.Add(closure);
        dbContext.ClosureHistory.Add(new ClosureHistory(Guid.CreateVersion7(closedAt), closure));
        dbContext.ClosureCommands.Add(resultCommand);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ClosureOutcome.Succeeded, resultCommand.ToResponse());
    }

    private static long CreateTransactionLockKey(Guid key)
    {
        Span<byte> bytes = stackalloc byte[16];
        key.TryWriteBytes(bytes, bigEndian: true, out _);
        return 0x434C53434D440000 ^ BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}

internal sealed record ClosureResult(ClosureOutcome Outcome, ClosureResponse? Response = null);
internal enum ClosureOutcome
{
    Succeeded, Unauthenticated, Forbidden, OrderNotFound, NotLiquidated,
    AlreadyClosed, IdempotencyConflict, StateInconsistent
}
