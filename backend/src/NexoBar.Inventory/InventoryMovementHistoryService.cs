using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory;

internal sealed class InventoryMovementHistoryService(
    InventoryDbContext dbContext,
    IInventoryAuthorization authorization,
    IIdentityOperationalNameLookup identityNames,
    ILogger<InventoryMovementHistoryService> logger)
{
    internal async Task<InventoryMovementHistoryResult> ReadAsync(
        Guid itemId,
        long? beforeRevision,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var dbTransaction = transaction.GetDbTransaction();
        var actor = await authorization.StabilizeSessionAndIdentityAsync(
            dbTransaction,
            cancellationToken);
        if (actor is null)
        {
            return InventoryMovementHistoryResult.Unauthenticated();
        }

        if (!await authorization.StabilizeInventoryOperationAsync(
                actor.IdentityId,
                dbTransaction,
                cancellationToken))
        {
            return InventoryMovementHistoryResult.Forbidden();
        }

        var item = await dbContext.InventoryItems.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == itemId,
                cancellationToken);
        if (item is null)
        {
            return InventoryMovementHistoryResult.ItemNotFound();
        }

        var query = dbContext.InventoryMovements.AsNoTracking()
            .Where(movement => movement.InventoryItemId == itemId);
        if (beforeRevision is not null)
        {
            query = query.Where(
                movement => movement.MovementRevision < beforeRevision.Value);
        }

        var persistedPage = await query
            .OrderByDescending(movement => movement.MovementRevision)
            .Take(limit + 1)
            .ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var hasMore = persistedPage.Length > limit;
        var movements = hasMore ? persistedPage[..limit] : persistedPage;
        if (movements.Any(movement => !IsValid(movement)))
        {
            logger.LogError(
                "Inventory Movement History is structurally inconsistent for Item {ItemId}.",
                itemId);
            return InventoryMovementHistoryResult.StateInconsistent();
        }

        var actorIds = movements
            .Select(movement => movement.ActorIdentityId)
            .Distinct()
            .ToArray();
        var resolvedNames = actorIds.Length == 0
            ? []
            : await identityNames.ReadByIdsAsync(
                actorIds,
                cancellationToken);
        var duplicateActor = resolvedNames
            .GroupBy(identity => identity.IdentityId)
            .Any(group => group.Count() != 1);
        var namesById = duplicateActor
            ? []
            : resolvedNames.ToDictionary(identity => identity.IdentityId);
        if (duplicateActor ||
            namesById.Count != actorIds.Length ||
            actorIds.Any(identityId => !namesById.ContainsKey(identityId)))
        {
            logger.LogError(
                "Inventory Movement History for Item {ItemId} references a missing Identity.",
                itemId);
            return InventoryMovementHistoryResult.ActorMissing();
        }

        var response = new InventoryMovementHistoryResponse(
            item.Id,
            item.OperationalName,
            item.OperationalUnit.Value,
            movements.Select(movement => Map(
                movement,
                namesById[movement.ActorIdentityId].OperationalName)).ToArray(),
            hasMore ? movements[^1].MovementRevision : null);
        return InventoryMovementHistoryResult.Succeeded(response);
    }

    private static bool IsValid(InventoryMovement movement)
    {
        if (movement.Id == Guid.Empty ||
            movement.InventoryItemId == Guid.Empty ||
            movement.MovementRevision <= 0 ||
            movement.ActorIdentityId == Guid.Empty ||
            movement.OccurredAt.Offset != TimeSpan.Zero ||
            !InventoryQuantity.IsWithinStorageRange(movement.Quantity) ||
            !InventoryQuantity.IsWithinStorageRange(
                movement.ResultingRegisteredQuantity) ||
            (movement.PreviousRegisteredQuantity is not null &&
                !InventoryQuantity.IsWithinStorageRange(
                    movement.PreviousRegisteredQuantity.Value)))
        {
            return false;
        }

        if (movement.Nature == InventoryMovement.ReconciliationNature)
        {
            return movement.CountObservationId is not null &&
                movement.Quantity >= 0 &&
                movement.ResultingRegisteredQuantity == movement.Quantity;
        }

        if (movement.CountObservationId is not null ||
            movement.PreviousRegisteredQuantity is null ||
            movement.Quantity <= 0)
        {
            return false;
        }

        var previous = movement.PreviousRegisteredQuantity.Value;
        decimal expected;
        try
        {
            expected = movement.Nature switch
            {
                InventoryMovement.EntryNature => checked(previous + movement.Quantity),
                InventoryMovement.ManualExitNature or InventoryMovement.WasteNature =>
                    checked(previous - movement.Quantity),
                _ => decimal.MinValue
            };
        }
        catch (OverflowException)
        {
            return false;
        }

        return expected != decimal.MinValue &&
            InventoryQuantity.IsWithinStorageRange(expected) &&
            movement.ResultingRegisteredQuantity == expected;
    }

    private static InventoryMovementHistoryEntryResponse Map(
        InventoryMovement movement,
        string actorOperationalName)
    {
        var previous = movement.PreviousRegisteredQuantity;
        var isReconciliation = movement.Nature == InventoryMovement.ReconciliationNature;
        decimal? signedEffect = isReconciliation
            ? previous is null
                ? null
                : movement.ResultingRegisteredQuantity - previous.Value
            : movement.Nature == InventoryMovement.EntryNature
                ? movement.Quantity
                : -movement.Quantity;
        var difference = isReconciliation && previous is not null
            ? movement.ResultingRegisteredQuantity - previous.Value
            : (decimal?)null;

        return new InventoryMovementHistoryEntryResponse(
            movement.Id,
            movement.MovementRevision,
            MapNature(movement.Nature),
            InventoryQuantity.Format(movement.Quantity),
            InventoryQuantity.Format(signedEffect),
            InventoryQuantity.Format(previous),
            InventoryQuantity.Format(movement.ResultingRegisteredQuantity),
            movement.OccurredAt,
            movement.ActorIdentityId,
            actorOperationalName,
            isReconciliation
                ? new InventoryMovementReconciliationResponse(
                    InventoryQuantity.Format(movement.Quantity),
                    InventoryQuantity.Format(difference),
                    previous is null)
                : null);
    }

    private static string MapNature(string nature) => nature switch
    {
        InventoryMovement.EntryNature => "entry",
        InventoryMovement.ManualExitNature => "manual_exit",
        InventoryMovement.WasteNature => "waste",
        InventoryMovement.ReconciliationNature => "reconciliation",
        _ => throw new InvalidOperationException(
            "An unsupported Inventory Movement nature reached response mapping.")
    };

}

internal sealed record InventoryMovementHistoryResult(
    InventoryMovementHistoryOutcome Outcome,
    InventoryMovementHistoryResponse? Response)
{
    internal static InventoryMovementHistoryResult Succeeded(
        InventoryMovementHistoryResponse response) =>
        new(InventoryMovementHistoryOutcome.Succeeded, response);

    internal static InventoryMovementHistoryResult Unauthenticated() =>
        new(InventoryMovementHistoryOutcome.Unauthenticated, null);

    internal static InventoryMovementHistoryResult Forbidden() =>
        new(InventoryMovementHistoryOutcome.Forbidden, null);

    internal static InventoryMovementHistoryResult ItemNotFound() =>
        new(InventoryMovementHistoryOutcome.ItemNotFound, null);

    internal static InventoryMovementHistoryResult ActorMissing() =>
        new(InventoryMovementHistoryOutcome.ActorMissing, null);

    internal static InventoryMovementHistoryResult StateInconsistent() =>
        new(InventoryMovementHistoryOutcome.StateInconsistent, null);
}

internal enum InventoryMovementHistoryOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden,
    ItemNotFound,
    ActorMissing,
    StateInconsistent
}
