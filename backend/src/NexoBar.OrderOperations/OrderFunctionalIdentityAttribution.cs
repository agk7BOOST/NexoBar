using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations;

internal sealed class OrderFunctionalIdentityAttribution(OrderOperationsDbContext db)
    : IOrderFunctionalIdentityAttribution
{
    public async Task<bool> HasFunctionalAttributionAsync(
        Guid identityId, DbTransaction transaction, CancellationToken cancellationToken)
    {
        db.Database.SetDbConnection(transaction.Connection ?? throw new InvalidOperationException(
            "Identity Delete requires an open PostgreSQL transaction."), contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        if (!ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), transaction))
            throw new InvalidOperationException("OrderOperations must share the Identity Delete transaction.");

        return await db.ConfirmationHistory.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken) ||
            await db.OrderContextChangeHistory.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken) ||
            await db.ContentCorrectionHistory.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken) ||
            await db.ContentCancellationHistory.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken) ||
            await db.AppliedPriceCorrectionHistory.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken) ||
            await db.PreparationHistory.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken) ||
            await db.DeliveryHistory.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken) ||
            await db.DeliveryCorrectionHistory.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken) ||
            await db.LiquidationHistory.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken) ||
            await db.ClosureHistory.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken) ||
            await db.CompleteCancellationHistory.AsNoTracking().AnyAsync(x => x.ActorIdentityId == identityId, cancellationToken);
    }
}
