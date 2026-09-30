using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OperationalConfiguration;

namespace NexoBar.OrderOperations;

internal sealed class OrderContextChangeService(
    OrderOperationsDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOrderOperationsCapabilityStabilizer capabilityStabilizer,
    IOrderContextConfiguration contextConfiguration,
    TimeProvider timeProvider,
    IOrderInvalidationPublisher orderInvalidations,
    IPreparationDestinationInvalidationPublisher preparationInvalidations)
{
    private const long CommandLockNamespace = 0x43545843484E4700;

    internal async Task<OrderContextChangeResult> ChangeAsync(
        Guid idempotencyKey,
        Guid orderId,
        OrderContextChangeRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var lockKey = CreateTransactionLockKey(idempotencyKey);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);

        var session = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(), cancellationToken);
        if (session is null)
            return new(OrderContextChangeOutcome.Unauthenticated);

        var command = await dbContext.OrderContextChangeCommands.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (command is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return command.Matches(session.IdentityId, orderId,
                    request.ExpectedCurrentContextId, request.NewContextId)
                ? new(OrderContextChangeOutcome.Succeeded, command.ToResponse())
                : new(OrderContextChangeOutcome.IdempotencyConflict);
        }

        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(
                session.IdentityId, transaction.GetDbTransaction(), cancellationToken))
            return new(OrderContextChangeOutcome.Forbidden);

        var target = await contextConfiguration.ResolveConfiguredContextAsync(
            request.NewContextId, transaction.GetDbTransaction(), cancellationToken);
        if (target is null)
            return new(OrderContextChangeOutcome.TargetContextNotFound);

        var order = await dbContext.Orders.FromSqlInterpolated(
                $"SELECT id, current_context_id, context FROM order_operations.orders WHERE id = {orderId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (order is null)
            return new(OrderContextChangeOutcome.OrderNotFound);

        if (await dbContext.OrderCancellationStates.AsNoTracking()
                .AnyAsync(x => x.OrderId == orderId, cancellationToken))
            return new(OrderContextChangeOutcome.OrderCancelled);
        if (await dbContext.Closures.AsNoTracking()
                .AnyAsync(x => x.OrderId == orderId, cancellationToken))
            return new(OrderContextChangeOutcome.OrderClosed);
        if (await dbContext.Liquidations.AsNoTracking()
                .AnyAsync(x => x.OrderId == orderId, cancellationToken))
            return new(OrderContextChangeOutcome.OrderFrozen);

        if (order.CurrentContextId != request.ExpectedCurrentContextId)
            return new(OrderContextChangeOutcome.ExpectedContextStale);
        if (order.CurrentContextId == target.ContextId)
            return new(OrderContextChangeOutcome.NoChange);

        var previousId = order.CurrentContextId;
        var previousName = order.CurrentContextOperationalName;
        var occurredAt = TruncateToMicroseconds(timeProvider.GetUtcNow());
        var historySequence = (await dbContext.OrderContextChangeHistory
            .Where(x => x.OrderId == orderId)
            .Select(x => (int?)x.Sequence)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var response = new OrderContextChangeResponse(
            orderId,
            orderId.ToString("D"),
            previousId,
            previousName,
            target.ContextId,
            target.OperationalName,
            occurredAt);

        order.ChangeContext(target.ContextId, target.OperationalName);
        dbContext.OrderContextChangeHistory.Add(new OrderContextChangeHistory(
            Guid.CreateVersion7(occurredAt), orderId, historySequence,
            previousId, previousName, target.ContextId, target.OperationalName,
            session.IdentityId, occurredAt));
        dbContext.OrderContextChangeCommands.Add(new OrderContextChangeCommand(
            idempotencyKey, session.IdentityId, orderId,
            request.ExpectedCurrentContextId, request.NewContextId, response));

        var destinations = await (
            from work in dbContext.PreparationWork.AsNoTracking()
            join incorporation in dbContext.Incorporations.AsNoTracking()
                on work.IncorporationId equals incorporation.Id
            where incorporation.OrderId == orderId
            select work.PreparationResponsibilityId).Distinct().ToArrayAsync(cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        orderInvalidations.PublishChanged(orderId);
        preparationInvalidations.Publish(destinations);
        return new(OrderContextChangeOutcome.Succeeded, response);
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);

    private static long CreateTransactionLockKey(Guid key)
    {
        Span<byte> bytes = stackalloc byte[16];
        key.TryWriteBytes(bytes, bigEndian: true, out _);
        return CommandLockNamespace ^ BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^
               BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}
