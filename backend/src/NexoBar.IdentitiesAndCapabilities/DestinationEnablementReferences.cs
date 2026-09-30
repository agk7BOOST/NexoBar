using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NexoBar.OperationalConfiguration;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class DestinationEnablementReferences(IdentitiesAndCapabilitiesDbContext db) : IDestinationEnablementReferences
{
    public async Task<bool> HasEnablementsAsync(Guid id, DbTransaction transaction, CancellationToken token)
    {
        await AdoptAsync(transaction, token);
        return await db.PreparationEnablements.AsNoTracking().AnyAsync(x => x.PreparationResponsibilityId == id, token);
    }
    private async Task AdoptAsync(DbTransaction transaction, CancellationToken token)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("The caller transaction must have an active connection.");
        db.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, token);
    }
}
