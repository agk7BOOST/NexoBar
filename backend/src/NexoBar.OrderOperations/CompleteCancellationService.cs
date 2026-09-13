using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class CompleteCancellationService(
    OrderOperationsDbContext dbContext,
    IAuthenticatedSessionStabilizer sessions,
    IOrderOperationsCapabilityStabilizer operations,
    IOperationalInterventionCapabilityStabilizer intervention,
    ActiveOrderReadState activeOrder,
    TimeProvider timeProvider,
    IPreparationDestinationInvalidationPublisher invalidations)
{
    internal async Task<CompleteCancellationResult> ExecuteAsync(Guid orderId, Guid? key, CancellationToken token)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        if (key is { } commandKey)
        {
            var lockKey = CreateTransactionLockKey(commandKey);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", token);
        }
        var session = await sessions.StabilizeAsync(transaction.GetDbTransaction(), token);
        if (session is null) return new("unauthenticated");
        if (key is not null)
        {
            var command = await dbContext.CompleteCancellationCommands.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, token);
            if (command is not null)
            {
                var history = await dbContext.CompleteCancellationHistory.AsNoTracking().SingleAsync(x => x.Id == command.CancellationId, token);
                if (history.ActorIdentityId != session.IdentityId || history.OrderId != orderId || command.CommandKind != CompleteCancellationCommand.Kind)
                    return new("idempotency_key_conflict");
                var result = await ResponseAsync(history, token);
                await transaction.CommitAsync(token);
                return new(Response: result);
            }
        }
        // Basic Order Operations authority is always required for a new command and for the advisory read.
        if (!await operations.StabilizeResponsibilityAsync(session.IdentityId, transaction.GetDbTransaction(), token))
            return new("forbidden");
        if (!await dbContext.Orders.FromSqlInterpolated(
                $"SELECT id, context FROM order_operations.orders WHERE id = {orderId} FOR UPDATE").AsNoTracking().AnyAsync(token))
            return new("order_not_found");

        // Only the advisory read uses AD-SEC-05; command and durable replay retain their semantics.
        if (key is null && !await activeOrder.IsReadableAsync(orderId, token)) return new("order_not_found");

        var plan = await PlanAsync(orderId, token);
        if (key is not null)
        {
            if (plan.Evaluation.RequiresOperationalIntervention == true &&
                !await intervention.StabilizeResponsibilityAsync(session.IdentityId, transaction.GetDbTransaction(), token))
                return new("operational_intervention_required");
            if (!plan.Evaluation.IsEligible) return new(plan.Evaluation.Blockers[0]);
        }
        else
        {
            await transaction.CommitAsync(token);
            return new(Evaluation: plan.Evaluation);
        }

        // Every blocker and the exact plan-dependent authorization have been checked. No discovery below.
        var affectedDestinations = new HashSet<Guid>();
        foreach (var row in plan.Rows)
        {
            var quantity = row.Content.Quantity - row.Quantities.RemovedByCorrectionQuantity - row.Quantities.CancelledQuantity;
            if (quantity == 0) continue;
            row.Quantities.Cancel(quantity, row.Content.Quantity, quantity);
            if (row.Work is { } work)
            {
                affectedDestinations.Add(work.PreparationResponsibilityId);
                if (work.PendingQuantity > 0) work.CancelPending(work.PendingQuantity);
                if (work.InPreparationQuantity > 0) work.InterveneInPreparation(work.InPreparationQuantity);
                if (work.ReadyQuantity > 0) work.InterveneReady(work.ReadyQuantity, 0);
            }
        }
        if (plan.Pending is not null) dbContext.PendingCompositions.Remove(plan.Pending);
        var now = timeProvider.GetUtcNow();
        var occurredAt = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        var fact = new CompleteCancellationHistory(Guid.CreateVersion7(occurredAt), orderId, session.IdentityId, occurredAt, plan.Pending is not null);
        dbContext.CompleteCancellationHistory.Add(fact);
        dbContext.CompleteCancellationDetails.AddRange(plan.Evaluation.Consequences.Select(x => new CompleteCancellationDetail(fact.Id, x)));
        dbContext.OrderCancellationStates.Add(new(orderId, fact.Id));
        dbContext.CompleteCancellationCommands.Add(new(key!.Value, fact.Id));
        await dbContext.SaveChangesAsync(token);
        var response = new CompleteCancellationResponse(orderId, fact.Id, occurredAt, true, fact.PendingCompositionDiscarded, plan.Evaluation.Consequences);
        await transaction.CommitAsync(token);
        invalidations.Publish(affectedDestinations);
        return new(Response: response);
    }

    private async Task<Plan> PlanAsync(Guid orderId, CancellationToken token)
    {
        var state = await dbContext.OrderCancellationStates.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == orderId, token);
        var fact = await dbContext.CompleteCancellationHistory.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == orderId, token);
        var liquidated = await dbContext.Liquidations.AsNoTracking().AnyAsync(x => x.OrderId == orderId, token);
        var closed = await dbContext.Closures.AsNoTracking().AnyAsync(x => x.OrderId == orderId, token);
        var pending = await dbContext.PendingCompositions.SingleOrDefaultAsync(x => x.OrderId == orderId, token);
        var incorporations = await dbContext.Incorporations.AsNoTracking().Where(x => x.OrderId == orderId)
            .OrderBy(x => x.Ordinal).ThenBy(x => x.Id).Select(x => x.Id).ToArrayAsync(token);
        var rows = new List<PlanRow>();
        var consequences = new List<CompleteCancellationConsequence>();
        var inconsistent = incorporations.Length == 0 || (closed && !liquidated) || (liquidated && pending is not null) ||
            ((state is null) != (fact is null)) || (state is not null && (liquidated || closed || pending is not null || state.CancellationId != fact?.Id));
        var delivered = false;
        long remaining = 0;
        foreach (var incorporationId in incorporations)
        {
            // All writers take Order first; subordinate locks follow Content -> Work -> Delivery -> quantities.
            var contents = await dbContext.IncorporationContents.FromSqlInterpolated(
                $"SELECT * FROM order_operations.incorporation_contents WHERE incorporation_id = {incorporationId} ORDER BY content_ordinal FOR SHARE")
                .AsNoTracking().ToArrayAsync(token);
            if (contents.Length == 0) inconsistent = true;
            foreach (var content in contents)
            {
                var ordinal = content.ContentOrdinal;
                var work = await dbContext.PreparationWork.FromSqlInterpolated(
                    $"SELECT * FROM order_operations.preparation_work WHERE incorporation_id = {incorporationId} AND content_ordinal = {ordinal} FOR UPDATE").SingleOrDefaultAsync(token);
                var delivery = await dbContext.DeliveryStates.FromSqlInterpolated(
                    $"SELECT * FROM order_operations.delivery_states WHERE incorporation_id = {incorporationId} AND content_ordinal = {ordinal} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(token);
                var quantities = await dbContext.ContentQuantityStates.FromSqlInterpolated(
                    $"SELECT * FROM order_operations.content_quantity_states WHERE incorporation_id = {incorporationId} AND content_ordinal = {ordinal} FOR UPDATE").SingleOrDefaultAsync(token);
                delivered |= delivery?.DeliveredQuantity > 0;
                if (content.AppliedPrice < 0 || !OperationalInterventionState.IsCoherent(content, work, delivery, quantities))
                {
                    inconsistent = true;
                    continue;
                }
                var fulfillment = content.Quantity - quantities!.RemovedByCorrectionQuantity - quantities.CancelledQuantity;
                rows.Add(new(content, quantities, work));
                remaining = checked(remaining + fulfillment);
                if (fulfillment > 0)
                    consequences.Add(new(incorporationId, ordinal, work?.PendingQuantity ?? fulfillment,
                        work?.InPreparationQuantity ?? 0, work?.ReadyQuantity ?? 0, 0));
            }
        }
        if (state is not null && remaining != 0) inconsistent = true;
        var blockers = new List<string>();
        if (inconsistent) blockers.Add("state_inconsistent");
        if (state is not null) blockers.Add("already_completely_cancelled");
        if (closed) blockers.Add("already_closed");
        if (liquidated) blockers.Add("order_frozen");
        if (delivered) blockers.Add("effective_delivery");
        // Never present a partial plan as authoritative when any Content is inconsistent.
        var evaluation = new CompleteCancellationEvaluation(orderId, state is not null || liquidated || closed, state is not null,
            state?.CancellationId, fact?.OccurredAt, delivered, pending is not null, inconsistent ? null : remaining,
            inconsistent ? null : consequences.Any(x => x.InPreparationQuantity > 0 || x.ReadyQuantity > 0),
            blockers.Count == 0, blockers, inconsistent ? [] : consequences);
        return new(evaluation, rows, pending);
    }

    private async Task<CompleteCancellationResponse> ResponseAsync(CompleteCancellationHistory fact, CancellationToken token)
    {
        var details = await dbContext.CompleteCancellationDetails.AsNoTracking().Where(x => x.CancellationId == fact.Id)
            .OrderBy(x => x.IncorporationId).ThenBy(x => x.ContentOrdinal).ToArrayAsync(token);
        return new(fact.OrderId, fact.Id, fact.OccurredAt, true, fact.PendingCompositionDiscarded, details.Select(x => x.ToResponse()).ToArray());
    }

    private static long CreateTransactionLockKey(Guid key)
    {
        Span<byte> bytes = stackalloc byte[16];
        key.TryWriteBytes(bytes, bigEndian: true, out _);
        return 0x43414E43454C0000 ^ BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^ BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
    private sealed record PlanRow(IncorporationContent Content, ContentQuantityState Quantities, PreparationWork? Work);
    private sealed record Plan(CompleteCancellationEvaluation Evaluation, List<PlanRow> Rows, PendingComposition? Pending);
}
