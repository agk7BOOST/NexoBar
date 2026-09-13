using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

/// <summary>Passive current authority for a fixed snapshot; never records Session activity.</summary>
public interface IActiveOrderSubscriptionAuthorization
{
    Task<ActiveOrderSubscriptionAuthorizationOutcome> AuthorizeAsync(
        IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken);

    // Actor authority only: this grants no Order visibility or post-terminal read access.
    Task<ActiveOrderSubscriptionAuthorizationOutcome> AuthorizeCurrentActorAsync(CancellationToken cancellationToken);
}

public enum ActiveOrderSubscriptionAuthorizationOutcome { Authorized, Unauthenticated, Forbidden, OrderNotFound }

internal sealed class ActiveOrderSubscriptionAuthorization(
    OrderOperationsDbContext dbContext,
    IOrderOperationsAuthorization authorization,
    ActiveOrderReadState activeOrder) : IActiveOrderSubscriptionAuthorization
{
    public Task<ActiveOrderSubscriptionAuthorizationOutcome> AuthorizeAsync(
        IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
        orderIds.Count == 0
            ? Task.FromResult(ActiveOrderSubscriptionAuthorizationOutcome.Forbidden)
            : CheckAsync(orderIds, cancellationToken);

    public Task<ActiveOrderSubscriptionAuthorizationOutcome> AuthorizeCurrentActorAsync(CancellationToken cancellationToken) =>
        CheckAsync([], cancellationToken);

    private async Task<ActiveOrderSubscriptionAuthorizationOutcome> CheckAsync(
        IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var actor = await authorization.AuthorizeAsync(transaction.GetDbTransaction(), cancellationToken);
        if (actor == OrderOperationsAuthorizationOutcome.Unauthenticated) return ActiveOrderSubscriptionAuthorizationOutcome.Unauthenticated;
        if (actor == OrderOperationsAuthorizationOutcome.Forbidden) return ActiveOrderSubscriptionAuthorizationOutcome.Forbidden;
        foreach (var orderId in orderIds.Distinct().Order())
        {
            if (!await activeOrder.IsReadableAsync(orderId, cancellationToken)) return ActiveOrderSubscriptionAuthorizationOutcome.OrderNotFound;
        }
        await transaction.CommitAsync(cancellationToken);
        return ActiveOrderSubscriptionAuthorizationOutcome.Authorized;
    }
}
