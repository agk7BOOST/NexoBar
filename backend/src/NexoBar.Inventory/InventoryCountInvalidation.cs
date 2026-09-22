using Microsoft.EntityFrameworkCore;

namespace NexoBar.Inventory;

internal static class InventoryCountInvalidation
{
    internal static async Task InvalidatePendingAsync(
        InventoryDbContext dbContext,
        Guid inventoryItemId,
        DateTimeOffset invalidatedAtUtc,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE inventory.count_observations AS observation
            SET invalidated_at_utc = {invalidatedAtUtc}
            WHERE observation.inventory_item_id = {inventoryItemId}
              AND observation.invalidated_at_utc IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM inventory.inventory_movements AS movement
                  WHERE movement.count_observation_id = observation.id)
            """,
            cancellationToken);
    }
}
