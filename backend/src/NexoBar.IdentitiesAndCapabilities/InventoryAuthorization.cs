using System.Data;
using System.Data.Common;

namespace NexoBar.IdentitiesAndCapabilities;

public interface IInventoryAuthorization
{
    Task<InventoryAuthorizedIdentity?> StabilizeSessionAndIdentityAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken);

    Task<bool> StabilizeInventoryConfigurationAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken);

    Task<bool> StabilizeInventoryOperationAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken);
}

public sealed record InventoryAuthorizedIdentity(Guid IdentityId);

internal sealed class InventoryAuthorization(
    IAuthenticatedSessionStabilizer sessionStabilizer) : IInventoryAuthorization
{
    public async Task<InventoryAuthorizedIdentity?> StabilizeSessionAndIdentityAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var session = await sessionStabilizer.StabilizeAsync(
            transaction,
            cancellationToken);
        return session is null
            ? null
            : new InventoryAuthorizedIdentity(session.IdentityId);
    }

    public Task<bool> StabilizeInventoryConfigurationAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        StabilizeResponsibilityAsync(
            identityId,
            "InventoryConfiguration",
            transaction,
            cancellationToken);

    public Task<bool> StabilizeInventoryOperationAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        StabilizeResponsibilityAsync(
            identityId,
            "InventoryOperation",
            transaction,
            cancellationToken);

    private static async Task<bool> StabilizeResponsibilityAsync(
        Guid identityId,
        string responsibilityCode,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var connection = transaction.Connection
            ?? throw new InvalidOperationException(
                "The caller transaction must have an active connection.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "The caller transaction connection must be open.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT 1
            FROM identities_and_capabilities.responsibility_assignments
            WHERE identity_id = @identity_id
              AND responsibility_code = @responsibility_code
            FOR SHARE
            """;
        AddParameter(command, "identity_id", identityId);
        AddParameter(command, "responsibility_code", responsibilityCode);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
