using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NexoBar.IdentitiesAndCapabilities;

/// <summary>Passive current authority for the list-wide Inventory operational subscription.</summary>
public interface IInventoryOperationSubscriptionAuthorization
{
    Task<InventoryOperationSubscriptionAuthorizationOutcome> AuthorizeAsync(
        CancellationToken cancellationToken);
}

public enum InventoryOperationSubscriptionAuthorizationOutcome { Authorized, Unauthenticated, Forbidden }

internal sealed class InventoryOperationSubscriptionAuthorization(
    IdentitiesAndCapabilitiesDbContext dbContext,
    IInventoryAuthorization inventoryAuthorization) : IInventoryOperationSubscriptionAuthorization
{
    public async Task<InventoryOperationSubscriptionAuthorizationOutcome> AuthorizeAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var actor = await inventoryAuthorization.StabilizeSessionAndIdentityAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (actor is null)
        {
            return InventoryOperationSubscriptionAuthorizationOutcome.Unauthenticated;
        }

        if (!await inventoryAuthorization.StabilizeInventoryOperationAsync(
                actor.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            return InventoryOperationSubscriptionAuthorizationOutcome.Forbidden;
        }

        await transaction.CommitAsync(cancellationToken);
        return InventoryOperationSubscriptionAuthorizationOutcome.Authorized;
    }
}
