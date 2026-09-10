using System.Data.Common;

namespace NexoBar.IdentitiesAndCapabilities;

public interface IOperationalInterventionCapabilityStabilizer
{
    Task<bool> StabilizeResponsibilityAsync(Guid identityId, DbTransaction transaction, CancellationToken cancellationToken);
}

internal sealed class OperationalInterventionCapabilityStabilizer : IOperationalInterventionCapabilityStabilizer
{
    public async Task<bool> StabilizeResponsibilityAsync(Guid identityId, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = PreparationAuthorization.CreateCommand(transaction);
        command.CommandText = """
            SELECT 1 FROM identities_and_capabilities.responsibility_assignments
            WHERE identity_id = @identity_id AND responsibility_code = 'OperationalIntervention'
            FOR SHARE
            """;
        PreparationAuthorization.AddParameter(command, "identity_id", identityId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
