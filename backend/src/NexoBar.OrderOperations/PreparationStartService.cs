using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class PreparationStartService(
    OrderOperationsDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IPreparationCapabilityStabilizer capabilityStabilizer,
    TimeProvider timeProvider)
{
    private const long PreparationCommandLockNamespace = 0x50524550434D4400;

    internal async Task<PreparationStartResult> StartAsync(
        Guid idempotencyKey,
        Guid workId,
        int quantity,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var lockKey = CreateTransactionLockKey(idempotencyKey);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);

        var stabilizedSession = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (stabilizedSession is null)
        {
            return PreparationStartResult.Unauthenticated();
        }

        var existingCommand = await dbContext.PreparationCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existingCommand.Matches(
                    stabilizedSession.IdentityId,
                    PreparationCommand.StartQuantityCommandKind,
                    workId,
                    quantity)
                ? PreparationStartResult.Started(existingCommand.ToStartResponse())
                : PreparationStartResult.IdempotencyConflict();
        }

        var dbTransaction = transaction.GetDbTransaction();
        if (!await capabilityStabilizer.StabilizePreparationResponsibilityAsync(
                stabilizedSession.IdentityId,
                dbTransaction,
                cancellationToken))
        {
            return PreparationStartResult.Forbidden();
        }

        var work = await dbContext.PreparationWork
            .FromSqlInterpolated(
                $"SELECT * FROM order_operations.preparation_work WHERE id = {workId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (work is null)
        {
            return PreparationStartResult.WorkNotFound();
        }

        if (!await capabilityStabilizer.StabilizeExactEnablementAsync(
                stabilizedSession.IdentityId,
                work.PreparationResponsibilityId,
                dbTransaction,
                cancellationToken))
        {
            return PreparationStartResult.WorkNotFound();
        }

        var transition = work.Start(quantity);
        if (transition == PreparationStartTransition.QuantityInvalid)
        {
            return PreparationStartResult.QuantityInvalid();
        }

        if (transition == PreparationStartTransition.PendingQuantityInsufficient)
        {
            return PreparationStartResult.PendingQuantityInsufficient();
        }

        var utcNow = timeProvider.GetUtcNow();
        var occurredAt = new DateTimeOffset(
            utcNow.Ticks - (utcNow.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);
        var historyId = Guid.CreateVersion7(occurredAt);
        var response = new StartPreparationQuantityResponse(
            work.Id,
            historyId,
            occurredAt,
            work.TotalQuantity,
            work.PendingQuantity,
            work.InPreparationQuantity,
            work.ReadyQuantity);
        dbContext.PreparationHistory.Add(new PreparationHistory(
            historyId,
            work.Id,
            quantity,
            stabilizedSession.IdentityId,
            occurredAt,
            work.TotalQuantity,
            work.PendingQuantity,
            work.InPreparationQuantity,
            work.ReadyQuantity));
        dbContext.PreparationCommands.Add(new PreparationCommand(
            idempotencyKey,
            stabilizedSession.IdentityId,
            work.Id,
            quantity,
            response));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PreparationStartResult.Started(response);
    }

    private static long CreateTransactionLockKey(Guid idempotencyKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);
        var keyPart = BinaryPrimitives.ReadInt64BigEndian(bytes[..8]);
        return keyPart ^ PreparationCommandLockNamespace;
    }
}

internal sealed record PreparationStartResult(
    PreparationStartOutcome Outcome,
    StartPreparationQuantityResponse? Response)
{
    internal static PreparationStartResult Started(
        StartPreparationQuantityResponse response) =>
        new(PreparationStartOutcome.Started, response);

    internal static PreparationStartResult Unauthenticated() =>
        new(PreparationStartOutcome.Unauthenticated, null);

    internal static PreparationStartResult Forbidden() =>
        new(PreparationStartOutcome.Forbidden, null);

    internal static PreparationStartResult WorkNotFound() =>
        new(PreparationStartOutcome.WorkNotFound, null);

    internal static PreparationStartResult QuantityInvalid() =>
        new(PreparationStartOutcome.QuantityInvalid, null);

    internal static PreparationStartResult PendingQuantityInsufficient() =>
        new(PreparationStartOutcome.PendingQuantityInsufficient, null);

    internal static PreparationStartResult IdempotencyConflict() =>
        new(PreparationStartOutcome.IdempotencyConflict, null);
}

internal enum PreparationStartOutcome
{
    Started,
    Unauthenticated,
    Forbidden,
    WorkNotFound,
    QuantityInvalid,
    PendingQuantityInsufficient,
    IdempotencyConflict
}
