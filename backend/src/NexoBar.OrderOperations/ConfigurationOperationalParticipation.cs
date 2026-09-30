using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NexoBar.OperationalConfiguration;

namespace NexoBar.OrderOperations;

internal sealed class ConfigurationOperationalParticipation(OrderOperationsDbContext db) : IContextOperationalParticipation, IDestinationOperationalParticipation
{
    async Task<bool> IContextOperationalParticipation.HasParticipationAsync(Guid id, DbTransaction transaction, CancellationToken token)
    {
        await AdoptAsync(transaction, token);
        return await db.ConfirmationHistory.AsNoTracking().AnyAsync(x => x.ConfirmedContextId == id, token)
            || await db.OrderContextChangeHistory.AsNoTracking().AnyAsync(x => x.PreviousContextId == id || x.NewContextId == id, token)
            || await db.Orders.AsNoTracking().AnyAsync(x => x.CurrentContextId == id, token);
    }
    async Task<bool> IDestinationOperationalParticipation.HasParticipationAsync(Guid id, DbTransaction transaction, CancellationToken token)
    {
        await AdoptAsync(transaction, token);
        return await db.PreparationWork.AsNoTracking().AnyAsync(x => x.PreparationResponsibilityId == id, token);
    }
    private async Task AdoptAsync(DbTransaction transaction, CancellationToken token)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("The caller transaction must have an active connection.");
        db.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, token);
    }
}
