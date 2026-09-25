using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class TerminalOrderHistoryQueryService(
    OrderOperationsDbContext db,
    IOrderOperationsAuthorization authorization)
{
    internal async Task<TerminalOrderHistoryResult> FindAsync(Guid orderId, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var authority = await authorization.AuthorizeAsync(tx.GetDbTransaction(), token);
        if (authority == OrderOperationsAuthorizationOutcome.Unauthenticated) return new(TerminalOrderHistoryOutcome.Unauthenticated);
        if (authority == OrderOperationsAuthorizationOutcome.Forbidden) return new(TerminalOrderHistoryOutcome.Forbidden);

        var order = await db.Orders.AsNoTracking().Where(x => x.Id == orderId)
            .Select(x => new { x.Id, x.CurrentContextId, x.CurrentContextOperationalName }).SingleOrDefaultAsync(token);
        if (order is null) return new(TerminalOrderHistoryOutcome.NotFound);

        // Eligibility is deliberately checked only after the actor has been authorized.
        var closure = await db.Closures.AsNoTracking().Where(x => x.OrderId == orderId)
            .Select(x => new TerminalFact("Closure", x.Id, x.ClosedAt, x.ActorIdentityId)).SingleOrDefaultAsync(token);
        var cancellation = await db.CompleteCancellationHistory.AsNoTracking().Where(x => x.OrderId == orderId)
            .Select(x => new TerminalFact("CompleteCancellation", x.Id, x.OccurredAt, x.ActorIdentityId, x.PendingCompositionDiscarded)).SingleOrDefaultAsync(token);
        if (closure is null && cancellation is null) return new(TerminalOrderHistoryOutcome.NotFound);

        var headers = await (from i in db.Incorporations.AsNoTracking()
                             join h in db.ConfirmationHistory.AsNoTracking() on i.Id equals h.IncorporationId
                             where i.OrderId == orderId
                             orderby i.Ordinal
                             select new { i.Id, i.Ordinal, h.OccurredAt, h.ActorIdentityId, h.ConfirmedContextId, h.ConfirmedContext }).ToArrayAsync(token);
        var ids = headers.Select(x => x.Id).ToArray();
        var contents = ids.Length == 0 ? [] : await db.IncorporationContents.AsNoTracking()
            .Where(x => ids.Contains(x.IncorporationId)).OrderBy(x => x.IncorporationId).ThenBy(x => x.ContentOrdinal).ToArrayAsync(token);
        var priceHistory = await db.AppliedPriceCorrectionHistory.AsNoTracking().Where(x => x.OrderId == orderId)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToArrayAsync(token);
        var quantityCorrections = await db.ContentCorrectionHistory.AsNoTracking().Where(x => x.OrderId == orderId)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToArrayAsync(token);
        var quantityCancellations = await db.ContentCancellationHistory.AsNoTracking().Where(x => x.OrderId == orderId)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToArrayAsync(token);
        var priceStates = ids.Length == 0 ? [] : await db.ContentAppliedPriceStates.AsNoTracking()
            .Where(x => ids.Contains(x.IncorporationId)).ToArrayAsync(token);
        var quantityStates = ids.Length == 0 ? [] : await db.ContentQuantityStates.AsNoTracking()
            .Where(x => ids.Contains(x.IncorporationId)).ToArrayAsync(token);
        var deliveryStates = ids.Length == 0 ? [] : await db.DeliveryStates.AsNoTracking()
            .Where(x => ids.Contains(x.IncorporationId)).ToArrayAsync(token);
        var delivery = await db.DeliveryHistory.AsNoTracking()
            .Where(x => ids.Contains(x.IncorporationId)).OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToArrayAsync(token);
        var deliveryCorrections = await db.DeliveryCorrectionHistory.AsNoTracking().Where(x => x.OrderId == orderId)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToArrayAsync(token);
        var works = ids.Length == 0 ? [] : await db.PreparationWork.AsNoTracking()
            .Where(x => ids.Contains(x.IncorporationId)).ToArrayAsync(token);
        var workIds = works.Select(x => x.Id).ToArray();
        var preparation = workIds.Length == 0 ? [] : await db.PreparationHistory.AsNoTracking()
            .Where(x => workIds.Contains(x.WorkId)).OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToArrayAsync(token);
        var contexts = await db.OrderContextChangeHistory.AsNoTracking().Where(x => x.OrderId == orderId)
            .OrderBy(x => x.Sequence).ToArrayAsync(token);
        var liquidation = await db.Liquidations.AsNoTracking().Where(x => x.OrderId == orderId)
            .Select(x => new LiquidationFact(x.Mode, x.FunctionalAmount, x.DeclaredPaymentMedium, x.OccurredAt, x.ActorIdentityId)).SingleOrDefaultAsync(token);
        var cancellationDetails = cancellation is null ? [] : await db.CompleteCancellationDetails.AsNoTracking()
            .Where(x => x.CancellationId == cancellation.Id).OrderBy(x => x.IncorporationId).ThenBy(x => x.ContentOrdinal).ToArrayAsync(token);

        var result = new TerminalOrderHistory(
            order.Id, order.Id.ToString("D"),
            closure is not null ? closure : cancellation!,
            order.CurrentContextId, order.CurrentContextOperationalName,
            contexts.Select(x => new ContextChangeFact(x.Sequence, x.PreviousContextId, x.PreviousContextOperationalName, x.NewContextId, x.NewContextOperationalName, x.ActorIdentityId, x.OccurredAtUtc)).ToArray(),
            headers.Select(h => new TerminalIncorporation(h.Id, h.Ordinal, h.OccurredAt, h.ActorIdentityId, h.ConfirmedContextId, h.ConfirmedContext,
                contents.Where(c => c.IncorporationId == h.Id).Select(c => new TerminalContent(c.ContentOrdinal, c.ProductId, c.ProductOperationalNameSnapshot, c.Quantity,
                    c.AppliedPrice.ToString(CultureInfo.InvariantCulture), c.Instruction, c.RequiresPreparationAtConfirmation,
                    works.FirstOrDefault(w => w.IncorporationId == h.Id && w.ContentOrdinal == c.ContentOrdinal)?.PreparationResponsibilityId,
                    c.UnavailableProductExceptionApplied,
                    priceStates.FirstOrDefault(p => p.IncorporationId == h.Id && p.ContentOrdinal == c.ContentOrdinal)?.EffectiveAppliedPrice.ToString(CultureInfo.InvariantCulture) ?? c.AppliedPrice.ToString(CultureInfo.InvariantCulture),
                    priceHistory.Where(p => p.IncorporationId == h.Id && p.ContentOrdinal == c.ContentOrdinal).Select(p => new PriceCorrectionFact(p.PreviousEffectiveAppliedPrice.ToString(CultureInfo.InvariantCulture), p.ResultingEffectiveAppliedPrice.ToString(CultureInfo.InvariantCulture), p.ActorIdentityId, p.OccurredAt)).ToArray(),
                    quantityCorrections.Where(p => p.IncorporationId == h.Id && p.ContentOrdinal == c.ContentOrdinal).Select(p => new QuantityFact("Correction", p.PreviousRemovedByCorrectionQuantity, p.ResultingRemovedByCorrectionQuantity, p.ActorIdentityId, p.OccurredAt)).ToArray(),
                    quantityCancellations.Where(p => p.IncorporationId == h.Id && p.ContentOrdinal == c.ContentOrdinal).Select(p => new QuantityFact("Cancellation", p.PreviousCancelledQuantity, p.ResultingCancelledQuantity, p.ActorIdentityId, p.OccurredAt)).ToArray(),
                    quantityStates.FirstOrDefault(p => p.IncorporationId == h.Id && p.ContentOrdinal == c.ContentOrdinal)?.RemovedByCorrectionQuantity ?? 0,
                    quantityStates.FirstOrDefault(p => p.IncorporationId == h.Id && p.ContentOrdinal == c.ContentOrdinal)?.CancelledQuantity ?? 0,
                    delivery.Where(p => p.IncorporationId == h.Id && p.ContentOrdinal == c.ContentOrdinal).Select(p => new DeliveryFact(p.EventKind, p.Quantity, p.ResultingDeliveredQuantity, p.ActorIdentityId, p.OccurredAt)).ToArray(),
                    deliveryCorrections.Where(p => p.IncorporationId == h.Id && p.ContentOrdinal == c.ContentOrdinal).Select(p => new DeliveryCorrectionFact(p.PreviousDeliveredQuantity, p.ResultingDeliveredQuantity, p.ActorIdentityId, p.OccurredAt)).ToArray(),
                    deliveryStates.FirstOrDefault(p => p.IncorporationId == h.Id && p.ContentOrdinal == c.ContentOrdinal)?.DeliveredQuantity ?? 0,
                    preparation.Where(p => works.Any(w => w.Id == p.WorkId && w.IncorporationId == h.Id && w.ContentOrdinal == c.ContentOrdinal)).Select(p => new PreparationFact(p.EventKind, p.Quantity, p.ResultingTotalQuantity, p.ResultingPendingQuantity, p.ResultingInPreparationQuantity, p.ResultingReadyQuantity, p.ActorIdentityId, p.OccurredAt)).ToArray()
                )).ToArray())).ToArray(),
            liquidation, closure is null ? null : new TerminationFact(closure.ActorIdentityId, closure.OccurredAt),
            cancellation is null ? null : new CompleteCancellationFact(cancellation.ActorIdentityId, cancellation.OccurredAt, cancellation.PendingCompositionDiscarded, cancellationDetails.Select(x => new CancellationConsequence(x.IncorporationId, x.ContentOrdinal, x.DirectOrPendingQuantity, x.InPreparationQuantity, x.ReadyQuantity, x.ResultingFulfillmentQuantity)).ToArray()));
        await tx.CommitAsync(token);
        return new(TerminalOrderHistoryOutcome.Succeeded, result);
    }
}

internal sealed record TerminalFact(string Type, Guid Id, DateTimeOffset OccurredAt, Guid ActorIdentityId, bool PendingCompositionDiscarded = false);
internal sealed record LiquidationFact(string Mode, decimal FunctionalAmount, string? DeclaredPaymentMedium, DateTimeOffset OccurredAt, Guid ActorIdentityId);
internal sealed record ContextChangeFact(int Sequence, Guid PreviousContextId, string PreviousContextOperationalName, Guid NewContextId, string NewContextOperationalName, Guid ActorIdentityId, DateTimeOffset OccurredAtUtc);
internal sealed record TerminalIncorporation(Guid Id, int Ordinal, DateTimeOffset ConfirmedAtUtc, Guid? ActorIdentityId, Guid ConfirmedContextId, string ConfirmedContext, IReadOnlyList<TerminalContent> Contents);
internal sealed record TerminalContent(int ContentOrdinal, Guid ProductId, string? ProductOperationalNameSnapshot, int OriginalConfirmedQuantity, string AppliedPrice, string? Instruction, bool RequiresPreparation, Guid? PreparationResponsibilityId, bool UnavailableProductExceptionApplied, string EffectiveAppliedPrice, IReadOnlyList<PriceCorrectionFact> PriceCorrections, IReadOnlyList<QuantityFact> Corrections, IReadOnlyList<QuantityFact> Cancellations, int RemovedByCorrectionQuantity, int CancelledQuantity, IReadOnlyList<DeliveryFact> Deliveries, IReadOnlyList<DeliveryCorrectionFact> DeliveryCorrections, int EffectiveDeliveredQuantity, IReadOnlyList<PreparationFact> PreparationHistory);
internal sealed record PriceCorrectionFact(string PreviousPrice, string ResultingPrice, Guid ActorIdentityId, DateTimeOffset OccurredAtUtc);
internal sealed record QuantityFact(string Type, int PreviousQuantity, int ResultingQuantity, Guid ActorIdentityId, DateTimeOffset OccurredAtUtc);
internal sealed record DeliveryFact(string Type, int Quantity, int ResultingDeliveredQuantity, Guid ActorIdentityId, DateTimeOffset OccurredAtUtc);
internal sealed record DeliveryCorrectionFact(int PreviousDeliveredQuantity, int ResultingDeliveredQuantity, Guid ActorIdentityId, DateTimeOffset OccurredAtUtc);
internal sealed record PreparationFact(string Type, int Quantity, int ResultingTotalQuantity, int ResultingPendingQuantity, int ResultingInPreparationQuantity, int ResultingReadyQuantity, Guid ActorIdentityId, DateTimeOffset OccurredAtUtc);
internal sealed record TerminationFact(Guid ActorIdentityId, DateTimeOffset OccurredAtUtc);
internal sealed record CancellationConsequence(Guid IncorporationId, int ContentOrdinal, int DirectOrPendingQuantity, int InPreparationQuantity, int ReadyQuantity, int ResultingFulfillmentQuantity);
internal sealed record CompleteCancellationFact(Guid ActorIdentityId, DateTimeOffset OccurredAtUtc, bool PendingCompositionDiscarded, IReadOnlyList<CancellationConsequence> Consequences);
internal sealed record TerminalOrderHistory(Guid OrderId, string OperationalReference, TerminalFact Termination, Guid FinalContextId, string FinalContextOperationalName, IReadOnlyList<ContextChangeFact> ContextChanges, IReadOnlyList<TerminalIncorporation> Incorporations, LiquidationFact? Liquidation, TerminationFact? Closure, CompleteCancellationFact? CompleteCancellation);
internal enum TerminalOrderHistoryOutcome { Succeeded, Unauthenticated, Forbidden, NotFound }
internal sealed record TerminalOrderHistoryResult(TerminalOrderHistoryOutcome Outcome, TerminalOrderHistory? History = null);
