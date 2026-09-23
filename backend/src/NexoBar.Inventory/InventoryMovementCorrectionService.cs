using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory;

internal sealed class InventoryMovementCorrectionService(
    InventoryDbContext db, IInventoryAuthorization authorization, TimeProvider clock,
    IInventoryOperationInvalidationPublisher invalidations)
{
    internal async Task<CorrectionResult> CorrectAsync(Guid key, Guid rootId, string nature, decimal quantity, long expectedRevision, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var bytes = key.ToByteArray(bigEndian: true);
        var lockKey = 0x494E56434F525245L ^ BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8)) ^ BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8, 8));
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);
        var actor = await authorization.StabilizeSessionAndIdentityAsync(tx.GetDbTransaction(), ct);
        if (actor is null) return CorrectionResult.Fail("unauthenticated");
        var replay = await db.InventoryMovementCorrections.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (replay is not null)
        {
            await tx.CommitAsync(ct);
            return replay.ActorIdentityId == actor.IdentityId && replay.RootMovementId == rootId && replay.CorrectedNature == nature && replay.CorrectedQuantity == quantity && replay.MovementRevision - 1 == expectedRevision
                ? CorrectionResult.Ok(ToResponse(replay, true)) : CorrectionResult.Fail("idempotency_conflict");
        }
        if (!await authorization.StabilizeInventoryOperationAsync(actor.IdentityId, tx.GetDbTransaction(), ct)) return CorrectionResult.Fail("forbidden");
        var root = await db.InventoryMovements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rootId, ct);
        if (root is null || root.Nature == InventoryMovement.ReconciliationNature) return CorrectionResult.Fail("invalid_root");
        var item = await db.InventoryItems.FromSqlInterpolated($"SELECT * FROM inventory.inventory_items WHERE id = {root.InventoryItemId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (item is null) return CorrectionResult.Fail("item_not_found");
        if (item.MovementRevision != expectedRevision) return CorrectionResult.Fail("stale");
        var prior = await db.InventoryMovementCorrections.AsNoTracking().Where(x => x.RootMovementId == rootId).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        var previousNature = prior?.CorrectedNature ?? root.Nature;
        var previousQuantity = prior?.CorrectedQuantity ?? root.Quantity;
        var sequence = (prior?.Sequence ?? 0) + 1;
        var previousEffect = Signed(previousNature, previousQuantity);
        var nextEffect = Signed(nature, quantity);
        var semanticDelta = nextEffect - previousEffect;
        var superseded = await db.InventoryMovements.AnyAsync(x => x.InventoryItemId == root.InventoryItemId && x.Nature == InventoryMovement.ReconciliationNature && x.MovementRevision > root.MovementRevision, ct);
        var applies = item.CurrentRegisteredQuantity is not null && !superseded;
        var appliedDelta = applies ? semanticDelta : 0m;
        long revision;
        try { revision = item.AdvanceCorrectionRevision(applies ? semanticDelta : null); }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException) { return CorrectionResult.Fail("out_of_range"); }
        var at = clock.GetUtcNow();
        var correction = new InventoryMovementCorrection(key, actor.IdentityId, root.Id, item.Id, sequence, revision, previousNature, previousQuantity, nature, quantity, appliedDelta, item.CurrentRegisteredQuantity, at);
        db.InventoryMovementCorrections.Add(correction);
        await InventoryCountInvalidation.InvalidatePendingAsync(db, item.Id, at, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        invalidations.PublishChanged();
        return CorrectionResult.Ok(ToResponse(correction, false));
    }

    private static decimal Signed(string nature, decimal quantity) => nature == InventoryMovement.EntryNature ? quantity : -quantity;
    private static InventoryMovementCorrectionResponse ToResponse(InventoryMovementCorrection x, bool replayed) => new(x.RootMovementId, x.Sequence, x.PreviousNature, InventoryQuantity.Format(x.PreviousQuantity), x.CorrectedNature, InventoryQuantity.Format(x.CorrectedQuantity), InventoryQuantity.Format(x.DeltaApplied), InventoryQuantity.Format(x.ResultingRegisteredQuantity), x.MovementRevision, x.ActorIdentityId, x.OccurredAtUtc, replayed);
}

internal sealed record CorrectionResult(string Outcome, InventoryMovementCorrectionResponse? Response)
{
    internal static CorrectionResult Ok(InventoryMovementCorrectionResponse response) => new("succeeded", response);
    internal static CorrectionResult Fail(string outcome) => new(outcome, null);
}
