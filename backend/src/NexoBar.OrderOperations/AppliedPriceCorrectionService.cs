using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class AppliedPriceCorrectionService(
    OrderOperationsDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOrderOperationsCapabilityStabilizer capabilityStabilizer,
    ActiveOrderReadState activeOrder,
    IOrderAppliedPriceCatalog catalog,
    TimeProvider timeProvider,
    IOrderInvalidationPublisher orderInvalidations)
{
    internal async Task<AppliedPriceCorrectionEvaluationResult> EvaluateAsync(Guid orderId, Guid incorporationId, int contentOrdinal, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var session = await sessionStabilizer.StabilizeAsync(transaction.GetDbTransaction(), cancellationToken);
        if (session is null) return new(AppliedPriceCorrectionEvaluationOutcome.Unauthenticated);
        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(session.IdentityId, transaction.GetDbTransaction(), cancellationToken)) return new(AppliedPriceCorrectionEvaluationOutcome.Forbidden);
        if (!await activeOrder.IsReadableAsync(orderId, cancellationToken) ||
            !await dbContext.Incorporations.AsNoTracking().AnyAsync(x => x.Id == incorporationId && x.OrderId == orderId, cancellationToken)) return new(AppliedPriceCorrectionEvaluationOutcome.ContentNotFound);
        var content = await dbContext.IncorporationContents.AsNoTracking().SingleOrDefaultAsync(x => x.IncorporationId == incorporationId && x.ContentOrdinal == contentOrdinal, cancellationToken);
        var state = await dbContext.ContentAppliedPriceStates.AsNoTracking().SingleOrDefaultAsync(x => x.IncorporationId == incorporationId && x.ContentOrdinal == contentOrdinal, cancellationToken);
        if (content is null) return new(AppliedPriceCorrectionEvaluationOutcome.ContentNotFound);
        if (state is null || content.AppliedPrice < 0 || state.EffectiveAppliedPrice < 0) return new(AppliedPriceCorrectionEvaluationOutcome.StateInconsistent);
        var product = await catalog.ReadCurrentProductAsync(content.ProductId, transaction.GetDbTransaction(), cancellationToken);
        var blockers = new List<string>();
        if (await dbContext.OrderCancellationStates.AsNoTracking().AnyAsync(x => x.OrderId == orderId, cancellationToken)) blockers.Add("order_completely_cancelled");
        if (await dbContext.Liquidations.AsNoTracking().AnyAsync(x => x.OrderId == orderId, cancellationToken)) blockers.Add("order_frozen");
        if (await dbContext.Closures.AsNoTracking().AnyAsync(x => x.OrderId == orderId, cancellationToken)) blockers.Add("order_closed");
        if (product is null || !product.IsActive || !product.IsAvailable || product.Price < 0) blockers.Add("product_not_current");
        else if (product.Price == state.EffectiveAppliedPrice) blockers.Add("no_correction_to_apply");
        var response = new AppliedPriceCorrectionEvaluationResponse(orderId, incorporationId, contentOrdinal,
            content.AppliedPrice.ToString(System.Globalization.CultureInfo.InvariantCulture), state.EffectiveAppliedPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
            product?.Price.ToString(System.Globalization.CultureInfo.InvariantCulture), blockers.Count == 0, blockers);
        await transaction.CommitAsync(cancellationToken);
        return new(AppliedPriceCorrectionEvaluationOutcome.Succeeded, response);
    }

    internal async Task<AppliedPriceCorrectionResult> CorrectAsync(
        Guid key, Guid orderId, Guid incorporationId, int contentOrdinal,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({CreateLockKey(key)})", cancellationToken);
        var session = await sessionStabilizer.StabilizeAsync(transaction.GetDbTransaction(), cancellationToken);
        if (session is null) return new(AppliedPriceCorrectionOutcome.Unauthenticated);

        var command = await dbContext.AppliedPriceCorrectionCommands.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
        if (command is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return command.Matches(session.IdentityId, orderId, incorporationId, contentOrdinal)
                ? new(AppliedPriceCorrectionOutcome.Succeeded, command.ToResponse())
                : new(AppliedPriceCorrectionOutcome.IdempotencyConflict);
        }
        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(session.IdentityId, transaction.GetDbTransaction(), cancellationToken))
            return new(AppliedPriceCorrectionOutcome.Forbidden);
        if (!await dbContext.Orders.FromSqlInterpolated(
                $"SELECT id, context FROM order_operations.orders WHERE id = {orderId} FOR UPDATE")
            .AsNoTracking().AnyAsync(cancellationToken))
            return new(AppliedPriceCorrectionOutcome.ContentNotFound);
        if (await dbContext.OrderCancellationStates.AsNoTracking().AnyAsync(x => x.OrderId == orderId, cancellationToken))
            return new(AppliedPriceCorrectionOutcome.OrderCancelled);
        if (await dbContext.Liquidations.AsNoTracking().AnyAsync(x => x.OrderId == orderId, cancellationToken))
            return new(AppliedPriceCorrectionOutcome.OrderFrozen);
        if (await dbContext.Closures.AsNoTracking().AnyAsync(x => x.OrderId == orderId, cancellationToken))
            return new(AppliedPriceCorrectionOutcome.OrderClosed);
        if (!await dbContext.Incorporations.AsNoTracking().AnyAsync(x => x.Id == incorporationId && x.OrderId == orderId, cancellationToken))
            return new(AppliedPriceCorrectionOutcome.ContentNotFound);
        var content = await dbContext.IncorporationContents.FromSqlInterpolated(
                $"SELECT * FROM order_operations.incorporation_contents WHERE incorporation_id = {incorporationId} AND content_ordinal = {contentOrdinal} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (content is null) return new(AppliedPriceCorrectionOutcome.ContentNotFound);
        var state = await dbContext.ContentAppliedPriceStates.FromSqlInterpolated(
                $"SELECT * FROM order_operations.content_applied_price_states WHERE incorporation_id = {incorporationId} AND content_ordinal = {contentOrdinal} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (state is null || content.AppliedPrice < 0 || state.EffectiveAppliedPrice < 0)
            return new(AppliedPriceCorrectionOutcome.StateInconsistent);

        var product = await catalog.ReadCurrentProductAsync(content.ProductId, transaction.GetDbTransaction(), cancellationToken);
        if (product is null || !product.IsActive || !product.IsAvailable || product.Price < 0)
            return new(AppliedPriceCorrectionOutcome.ProductNotCurrent);
        if (product.Price == state.EffectiveAppliedPrice)
            return new(AppliedPriceCorrectionOutcome.NoCorrectionToApply);

        var previous = state.EffectiveAppliedPrice;
        state.Correct(product.Price);
        var now = timeProvider.GetUtcNow();
        var occurredAt = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        var response = new AppliedPriceCorrectionResponse(orderId, incorporationId, contentOrdinal,
            Guid.CreateVersion7(occurredAt),
            previous.ToString(System.Globalization.CultureInfo.InvariantCulture),
            product.Price.ToString(System.Globalization.CultureInfo.InvariantCulture), occurredAt);
        dbContext.AppliedPriceCorrectionHistory.Add(new AppliedPriceCorrectionHistory(response, session.IdentityId));
        dbContext.AppliedPriceCorrectionCommands.Add(new AppliedPriceCorrectionCommand(key, session.IdentityId, response));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        orderInvalidations.PublishChanged(orderId);
        return new(AppliedPriceCorrectionOutcome.Succeeded, response);
    }

    private static long CreateLockKey(Guid key)
    {
        Span<byte> bytes = stackalloc byte[16];
        key.TryWriteBytes(bytes, bigEndian: true, out _);
        return 0x4150504C50524300 ^ BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^ BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}

internal sealed record AppliedPriceCorrectionResult(AppliedPriceCorrectionOutcome Outcome, AppliedPriceCorrectionResponse? Response = null);
internal enum AppliedPriceCorrectionOutcome
{
    Succeeded, Unauthenticated, Forbidden, ContentNotFound, ProductNotCurrent, NoCorrectionToApply,
    OrderCancelled, OrderFrozen, OrderClosed, IdempotencyConflict, StateInconsistent
}

internal sealed record AppliedPriceCorrectionEvaluationResult(AppliedPriceCorrectionEvaluationOutcome Outcome, AppliedPriceCorrectionEvaluationResponse? Response = null);
internal enum AppliedPriceCorrectionEvaluationOutcome { Succeeded, Unauthenticated, Forbidden, ContentNotFound, StateInconsistent }
