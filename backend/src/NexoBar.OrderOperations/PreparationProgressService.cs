using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class PreparationProgressService(
    OrderOperationsDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IPreparationCapabilityStabilizer capabilityStabilizer,
    TimeProvider timeProvider)
{
    private const long PreparationCommandLockNamespace = 0x50524550434D4400;

    internal Task<PreparationProgressResult> StartAsync(
        Guid idempotencyKey,
        Guid workId,
        int quantity,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            PreparationProgressCommand.StartQuantity,
            idempotencyKey,
            workId,
            quantity,
            cancellationToken);

    internal Task<PreparationProgressResult> MarkReadyAsync(
        Guid idempotencyKey,
        Guid workId,
        int quantity,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            PreparationProgressCommand.MarkQuantityReady,
            idempotencyKey,
            workId,
            quantity,
            cancellationToken);

    private async Task<PreparationProgressResult> ExecuteAsync(
        PreparationProgressCommand progressCommand,
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
            return PreparationProgressResult.Unauthenticated();
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
                    CommandKind(progressCommand),
                    workId,
                    quantity)
                ? PreparationProgressResult.Succeeded(existingCommand.ToResult())
                : PreparationProgressResult.IdempotencyConflict();
        }

        var dbTransaction = transaction.GetDbTransaction();
        if (!await capabilityStabilizer.StabilizePreparationResponsibilityAsync(
                stabilizedSession.IdentityId,
                dbTransaction,
                cancellationToken))
        {
            return PreparationProgressResult.Forbidden();
        }

        var orderId = await (
            from workCandidate in dbContext.PreparationWork.AsNoTracking()
            join incorporation in dbContext.Incorporations.AsNoTracking()
                on workCandidate.IncorporationId equals incorporation.Id
            where workCandidate.Id == workId
            select (Guid?)incorporation.OrderId)
            .SingleOrDefaultAsync(cancellationToken);
        if (orderId is null)
        {
            return PreparationProgressResult.WorkNotFound();
        }

        await dbContext.Orders
            .FromSqlInterpolated(
                $"SELECT id, context FROM order_operations.orders WHERE id = {orderId.Value} FOR UPDATE")
            .AsNoTracking()
            .AnyAsync(cancellationToken);
        var work = await dbContext.PreparationWork
            .FromSqlInterpolated(
                $"SELECT * FROM order_operations.preparation_work WHERE id = {workId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (work is null)
        {
            return PreparationProgressResult.WorkNotFound();
        }

        if (!await capabilityStabilizer.StabilizeExactEnablementAsync(
                stabilizedSession.IdentityId,
                work.PreparationResponsibilityId,
                dbTransaction,
                cancellationToken))
        {
            return PreparationProgressResult.WorkNotFound();
        }

        if (await dbContext.Liquidations.AsNoTracking().AnyAsync(
                liquidation => liquidation.OrderId == orderId.Value,
                cancellationToken))
        {
            return PreparationProgressResult.OrderFrozen();
        }

        var contentState = await (
            from content in dbContext.IncorporationContents.AsNoTracking()
            where content.IncorporationId == work.IncorporationId &&
                content.ContentOrdinal == work.ContentOrdinal
            join quantityStateValue in dbContext.ContentQuantityStates.AsNoTracking()
                on new { content.IncorporationId, content.ContentOrdinal }
                equals new { quantityStateValue.IncorporationId, quantityStateValue.ContentOrdinal }
                into quantityStates
            from quantityState in quantityStates.DefaultIfEmpty()
            join deliveryValue in dbContext.DeliveryStates.AsNoTracking()
                on new { content.IncorporationId, content.ContentOrdinal }
                equals new { deliveryValue.IncorporationId, deliveryValue.ContentOrdinal }
                into deliveries
            from delivery in deliveries.DefaultIfEmpty()
            select new
            {
                Confirmed = content.Quantity,
                Removed = quantityState == null ? (int?)null : quantityState.RemovedByCorrectionQuantity,
                Delivered = delivery == null ? (int?)null : delivery.DeliveredQuantity
            }).SingleOrDefaultAsync(cancellationToken);
        var effective = contentState?.Removed is null
            ? (int?)null
            : contentState.Confirmed - contentState.Removed.Value;
        if (contentState is null || effective <= 0 || contentState.Removed < 0 ||
            contentState.Removed > contentState.Confirmed || contentState.Delivered is null ||
            contentState.Delivered < 0 || contentState.Delivered > effective ||
            work.TotalQuantity != effective || work.PendingQuantity < 0 ||
            work.InPreparationQuantity < 0 || work.ReadyQuantity < 0 ||
            (long)work.PendingQuantity + work.InPreparationQuantity + work.ReadyQuantity != effective ||
            contentState.Delivered > work.ReadyQuantity)
        {
            return PreparationProgressResult.StateInconsistent();
        }

        var transition = Apply(progressCommand, work, quantity);
        if (transition == PreparationProgressTransition.QuantityInvalid)
        {
            return PreparationProgressResult.QuantityInvalid();
        }

        if (transition == PreparationProgressTransition.AvailableQuantityInsufficient)
        {
            return PreparationProgressResult.AvailableQuantityInsufficient();
        }

        var utcNow = timeProvider.GetUtcNow();
        var occurredAt = new DateTimeOffset(
            utcNow.Ticks - (utcNow.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);
        var historyId = Guid.CreateVersion7(occurredAt);
        var result = new PreparationCommandResult(
            work.Id,
            historyId,
            occurredAt,
            work.TotalQuantity,
            work.PendingQuantity,
            work.InPreparationQuantity,
            work.ReadyQuantity);
        dbContext.PreparationHistory.Add(CreateHistory(
            progressCommand,
            historyId,
            work.Id,
            quantity,
            stabilizedSession.IdentityId,
            occurredAt,
            result));
        dbContext.PreparationCommands.Add(CreateCommand(
            progressCommand,
            idempotencyKey,
            stabilizedSession.IdentityId,
            work.Id,
            quantity,
            result));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PreparationProgressResult.Succeeded(result);
    }

    private static PreparationProgressTransition Apply(
        PreparationProgressCommand progressCommand,
        PreparationWork work,
        int quantity) =>
        progressCommand switch
        {
            PreparationProgressCommand.StartQuantity => work.Start(quantity) switch
            {
                PreparationStartTransition.Started => PreparationProgressTransition.Succeeded,
                PreparationStartTransition.QuantityInvalid =>
                    PreparationProgressTransition.QuantityInvalid,
                PreparationStartTransition.PendingQuantityInsufficient =>
                    PreparationProgressTransition.AvailableQuantityInsufficient,
                _ => throw new ArgumentOutOfRangeException(nameof(progressCommand))
            },
            PreparationProgressCommand.MarkQuantityReady => work.MarkReady(quantity) switch
            {
                PreparationReadyTransition.MarkedReady =>
                    PreparationProgressTransition.Succeeded,
                PreparationReadyTransition.QuantityInvalid =>
                    PreparationProgressTransition.QuantityInvalid,
                PreparationReadyTransition.InPreparationQuantityInsufficient =>
                    PreparationProgressTransition.AvailableQuantityInsufficient,
                _ => throw new ArgumentOutOfRangeException(nameof(progressCommand))
            },
            _ => throw new ArgumentOutOfRangeException(nameof(progressCommand))
        };

    private static PreparationHistory CreateHistory(
        PreparationProgressCommand progressCommand,
        Guid historyId,
        Guid workId,
        int quantity,
        Guid actorIdentityId,
        DateTimeOffset occurredAt,
        PreparationCommandResult result) =>
        progressCommand switch
        {
            PreparationProgressCommand.StartQuantity => PreparationHistory.QuantityStarted(
                historyId, workId, quantity, actorIdentityId, occurredAt, result),
            PreparationProgressCommand.MarkQuantityReady => PreparationHistory.QuantityReady(
                historyId, workId, quantity, actorIdentityId, occurredAt, result),
            _ => throw new ArgumentOutOfRangeException(nameof(progressCommand))
        };

    private static PreparationCommand CreateCommand(
        PreparationProgressCommand progressCommand,
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid workId,
        int quantity,
        PreparationCommandResult result) =>
        progressCommand switch
        {
            PreparationProgressCommand.StartQuantity => PreparationCommand.StartQuantity(
                idempotencyKey, actorIdentityId, workId, quantity, result),
            PreparationProgressCommand.MarkQuantityReady =>
                PreparationCommand.MarkQuantityReady(
                    idempotencyKey, actorIdentityId, workId, quantity, result),
            _ => throw new ArgumentOutOfRangeException(nameof(progressCommand))
        };

    private static string CommandKind(PreparationProgressCommand progressCommand) =>
        progressCommand switch
        {
            PreparationProgressCommand.StartQuantity =>
                PreparationCommand.StartQuantityCommandKind,
            PreparationProgressCommand.MarkQuantityReady =>
                PreparationCommand.MarkQuantityReadyCommandKind,
            _ => throw new ArgumentOutOfRangeException(nameof(progressCommand))
        };

    private static long CreateTransactionLockKey(Guid idempotencyKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);
        var keyPart = BinaryPrimitives.ReadInt64BigEndian(bytes[..8]);
        return keyPart ^ PreparationCommandLockNamespace;
    }
}

internal sealed record PreparationProgressResult(
    PreparationProgressOutcome Outcome,
    PreparationCommandResult? Response)
{
    internal static PreparationProgressResult Succeeded(PreparationCommandResult response) =>
        new(PreparationProgressOutcome.Succeeded, response);

    internal static PreparationProgressResult Unauthenticated() =>
        new(PreparationProgressOutcome.Unauthenticated, null);

    internal static PreparationProgressResult Forbidden() =>
        new(PreparationProgressOutcome.Forbidden, null);

    internal static PreparationProgressResult WorkNotFound() =>
        new(PreparationProgressOutcome.WorkNotFound, null);

    internal static PreparationProgressResult QuantityInvalid() =>
        new(PreparationProgressOutcome.QuantityInvalid, null);

    internal static PreparationProgressResult AvailableQuantityInsufficient() =>
        new(PreparationProgressOutcome.AvailableQuantityInsufficient, null);

    internal static PreparationProgressResult IdempotencyConflict() =>
        new(PreparationProgressOutcome.IdempotencyConflict, null);

    internal static PreparationProgressResult OrderFrozen() =>
        new(PreparationProgressOutcome.OrderFrozen, null);

    internal static PreparationProgressResult StateInconsistent() =>
        new(PreparationProgressOutcome.StateInconsistent, null);
}

internal enum PreparationProgressOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden,
    WorkNotFound,
    QuantityInvalid,
    AvailableQuantityInsufficient,
    IdempotencyConflict,
    OrderFrozen,
    StateInconsistent
}

internal enum PreparationProgressCommand
{
    StartQuantity,
    MarkQuantityReady
}

internal enum PreparationProgressTransition
{
    Succeeded,
    QuantityInvalid,
    AvailableQuantityInsufficient
}
