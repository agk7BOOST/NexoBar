using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class LiquidationService(
    OrderOperationsDbContext dbContext,
    OrderEconomicStateReader economicStateReader,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOrderOperationsCapabilityStabilizer capabilityStabilizer,
    TimeProvider timeProvider)
{
    internal const int DeclaredPaymentMediumMaxLength = 200;
    private const long CommandLockNamespace = 0x4C4951434D440000;

    internal Task<LiquidationResult> LiquidateSimpleAsync(
        Guid idempotencyKey,
        Guid orderId,
        string declaredPaymentMedium,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            idempotencyKey,
            orderId,
            LiquidationCommand.LiquidateSimpleCommandKind,
            LiquidationModes.Simple,
            declaredPaymentMedium,
            cancellationToken);

    internal Task<LiquidationResult> RecordExternalCollectionAsync(
        Guid idempotencyKey,
        Guid orderId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            idempotencyKey,
            orderId,
            LiquidationCommand.RecordExternalCollectionCommandKind,
            LiquidationModes.ExternalCollection,
            null,
            cancellationToken);

    private async Task<LiquidationResult> ExecuteAsync(
        Guid idempotencyKey,
        Guid orderId,
        string commandKind,
        string mode,
        string? declaredPaymentMedium,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var lockKey = CreateTransactionLockKey(idempotencyKey);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);

        var session = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (session is null)
        {
            return LiquidationResult.Unauthenticated();
        }

        var existingCommand = await dbContext.LiquidationCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existingCommand.Matches(
                    session.IdentityId,
                    commandKind,
                    orderId,
                    declaredPaymentMedium)
                ? LiquidationResult.Succeeded(existingCommand.ToResult())
                : LiquidationResult.IdempotencyConflict();
        }

        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(
                session.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            return LiquidationResult.Forbidden();
        }

        var orderExists = await dbContext.Orders
            .FromSqlInterpolated(
                $"SELECT id, context FROM order_operations.orders WHERE id = {orderId} FOR UPDATE")
            .AsNoTracking()
            .AnyAsync(cancellationToken);
        if (!orderExists)
        {
            return LiquidationResult.OrderNotFound();
        }

        if (await dbContext.Liquidations.AsNoTracking().AnyAsync(
                liquidation => liquidation.OrderId == orderId,
                cancellationToken))
        {
            return LiquidationResult.OrderFrozen();
        }

        if (await dbContext.PendingCompositions.AsNoTracking().AnyAsync(
                pending => pending.OrderId == orderId,
                cancellationToken))
        {
            return LiquidationResult.PendingComposition();
        }

        OrderEconomicState economicState;
        try
        {
            economicState = await economicStateReader.ReadAsync(orderId, cancellationToken);
        }
        catch (OverflowException)
        {
            return LiquidationResult.StateInconsistent();
        }

        if (economicState.IsInconsistent)
        {
            return LiquidationResult.StateInconsistent();
        }

        if (economicState.HasUnresolvedFulfillment)
        {
            return LiquidationResult.UnresolvedFulfillment();
        }

        var occurredAt = TruncateToMicroseconds(timeProvider.GetUtcNow());
        var liquidationId = Guid.CreateVersion7(occurredAt);
        var result = new LiquidationCommandResult(
            liquidationId,
            orderId,
            mode,
            economicState.FunctionalAmount,
            declaredPaymentMedium,
            occurredAt);
        dbContext.Liquidations.Add(new Liquidation(
            liquidationId,
            orderId,
            mode,
            economicState.FunctionalAmount,
            declaredPaymentMedium,
            occurredAt,
            session.IdentityId));
        dbContext.LiquidationHistory.Add(new LiquidationHistory(
            Guid.CreateVersion7(occurredAt),
            liquidationId,
            orderId,
            mode,
            economicState.FunctionalAmount,
            declaredPaymentMedium,
            occurredAt,
            session.IdentityId));
        dbContext.LiquidationCommands.Add(new LiquidationCommand(
            idempotencyKey,
            session.IdentityId,
            commandKind,
            orderId,
            declaredPaymentMedium,
            result));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return LiquidationResult.Succeeded(result);
    }

    internal static string? CanonicalizeDeclaredPaymentMedium(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var canonical = value.Trim();
        return canonical.Length is > 0 and <= DeclaredPaymentMediumMaxLength
            ? canonical
            : null;
    }

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

internal sealed record LiquidationResult(
    LiquidationOutcome Outcome,
    LiquidationCommandResult? Response)
{
    internal static LiquidationResult Succeeded(LiquidationCommandResult response) =>
        new(LiquidationOutcome.Succeeded, response);
    internal static LiquidationResult Unauthenticated() =>
        new(LiquidationOutcome.Unauthenticated, null);
    internal static LiquidationResult Forbidden() =>
        new(LiquidationOutcome.Forbidden, null);
    internal static LiquidationResult OrderNotFound() =>
        new(LiquidationOutcome.OrderNotFound, null);
    internal static LiquidationResult OrderFrozen() =>
        new(LiquidationOutcome.OrderFrozen, null);
    internal static LiquidationResult PendingComposition() =>
        new(LiquidationOutcome.PendingComposition, null);
    internal static LiquidationResult UnresolvedFulfillment() =>
        new(LiquidationOutcome.UnresolvedFulfillment, null);
    internal static LiquidationResult IdempotencyConflict() =>
        new(LiquidationOutcome.IdempotencyConflict, null);
    internal static LiquidationResult StateInconsistent() =>
        new(LiquidationOutcome.StateInconsistent, null);
}

internal enum LiquidationOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden,
    OrderNotFound,
    OrderFrozen,
    PendingComposition,
    UnresolvedFulfillment,
    IdempotencyConflict,
    StateInconsistent
}
