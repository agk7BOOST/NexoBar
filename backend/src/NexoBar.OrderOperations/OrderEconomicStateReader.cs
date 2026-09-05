using Microsoft.EntityFrameworkCore;

namespace NexoBar.OrderOperations;

internal sealed class OrderEconomicStateReader(OrderOperationsDbContext dbContext)
{
    internal async Task<OrderEconomicState> ReadAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from incorporation in dbContext.Incorporations.AsNoTracking()
            where incorporation.OrderId == orderId
            join content in dbContext.IncorporationContents.AsNoTracking()
                on incorporation.Id equals content.IncorporationId
            join stateValue in dbContext.DeliveryStates.AsNoTracking()
                on new { content.IncorporationId, content.ContentOrdinal }
                equals new { stateValue.IncorporationId, stateValue.ContentOrdinal }
                into states
            from state in states.DefaultIfEmpty()
            join workValue in dbContext.PreparationWork.AsNoTracking()
                on new { content.IncorporationId, content.ContentOrdinal }
                equals new { workValue.IncorporationId, workValue.ContentOrdinal }
                into works
            from work in works.DefaultIfEmpty()
            select new EconomicContentRow(
                content.Quantity,
                content.AppliedPrice,
                content.RequiresPreparationAtConfirmation,
                state == null ? null : state.DeliveredQuantity,
                work == null ? null : work.TotalQuantity,
                work == null ? null : work.PendingQuantity,
                work == null ? null : work.InPreparationQuantity,
                work == null ? null : work.ReadyQuantity))
            .ToArrayAsync(cancellationToken);

        var inconsistent = rows.Length == 0 || rows.Any(IsInconsistent);
        var functionalAmount = rows.Aggregate(
            0m,
            (amount, row) => checked(
                amount + (row.DeliveredQuantity ?? 0) * row.AppliedPrice));
        var unresolved = !inconsistent && rows.Any(row =>
            row.DeliveredQuantity != row.Quantity);
        return new OrderEconomicState(functionalAmount, unresolved, inconsistent);
    }

    private static bool IsInconsistent(EconomicContentRow row)
    {
        if (row.Quantity <= 0 ||
            row.AppliedPrice < 0 ||
            row.DeliveredQuantity is null ||
            row.DeliveredQuantity < 0 ||
            row.DeliveredQuantity > row.Quantity)
        {
            return true;
        }

        var hasWork = row.WorkTotalQuantity is not null;
        if (row.RequiresPreparationAtConfirmation != hasWork)
        {
            return true;
        }

        if (!hasWork)
        {
            return false;
        }

        return row.WorkTotalQuantity != row.Quantity ||
            row.WorkPendingQuantity < 0 ||
            row.WorkInPreparationQuantity < 0 ||
            row.WorkReadyQuantity < 0 ||
            (long)row.WorkPendingQuantity!.Value +
            row.WorkInPreparationQuantity!.Value +
            row.WorkReadyQuantity!.Value != row.WorkTotalQuantity.Value ||
            row.DeliveredQuantity > row.WorkReadyQuantity;
    }

    private sealed record EconomicContentRow(
        int Quantity,
        decimal AppliedPrice,
        bool RequiresPreparationAtConfirmation,
        int? DeliveredQuantity,
        int? WorkTotalQuantity,
        int? WorkPendingQuantity,
        int? WorkInPreparationQuantity,
        int? WorkReadyQuantity);
}

internal sealed record OrderEconomicState(
    decimal FunctionalAmount,
    bool HasUnresolvedFulfillment,
    bool IsInconsistent);
