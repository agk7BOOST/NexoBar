using System.Data.Common;
using NexoBar.OperationalConfiguration;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class OperationalConfigurationAuthorization(
    IAuthenticatedSessionStabilizer sessionStabilizer) :
    IOperationalConfigurationAuthorization
{
    public async Task<OperationalConfigurationAuthenticatedActor?>
        StabilizeSessionAsync(
            DbTransaction transaction,
            CancellationToken cancellationToken)
    {
        var session = await sessionStabilizer.StabilizeAsync(
            transaction,
            cancellationToken);
        return session is null
            ? null
            : new OperationalConfigurationAuthenticatedActor(session.IdentityId);
    }

    public async Task<bool> StabilizeGeneralConfigurationAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = PreparationAuthorization.CreateCommand(transaction);
        command.CommandText = """
            SELECT 1
            FROM identities_and_capabilities.responsibility_assignments
            WHERE identity_id = @identity_id
              AND responsibility_code = 'GeneralConfiguration'
            FOR SHARE
            """;
        PreparationAuthorization.AddParameter(command, "identity_id", identityId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
