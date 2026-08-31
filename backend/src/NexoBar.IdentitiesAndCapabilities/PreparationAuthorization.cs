using System.Data;
using System.Data.Common;

namespace NexoBar.IdentitiesAndCapabilities;

public interface IPreparationAuthorization
{
    Task<PreparationAuthorizationOutcome> AuthorizeAsync(
        Guid preparationResponsibilityId,
        DbTransaction transaction,
        CancellationToken cancellationToken);
}

public enum PreparationAuthorizationOutcome
{
    Authorized,
    Unauthenticated,
    Forbidden
}

public interface IPreparationCapabilityStabilizer
{
    Task<bool> StabilizePreparationResponsibilityAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken);

    Task<bool> StabilizeExactEnablementAsync(
        Guid identityId,
        Guid preparationResponsibilityId,
        DbTransaction transaction,
        CancellationToken cancellationToken);
}

internal sealed class PreparationAuthorization(
    IAuthenticatedSessionStabilizer sessionStabilizer) : IPreparationAuthorization
{
    public async Task<PreparationAuthorizationOutcome> AuthorizeAsync(
        Guid preparationResponsibilityId,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var session = await sessionStabilizer.StabilizeAsync(
            transaction,
            cancellationToken);
        if (session is null)
        {
            return PreparationAuthorizationOutcome.Unauthenticated;
        }

        if (!await HasPreparationResponsibilityAsync(
                session.IdentityId,
                transaction,
                cancellationToken))
        {
            return PreparationAuthorizationOutcome.Forbidden;
        }

        return await HasExactEnablementAsync(
                session.IdentityId,
                preparationResponsibilityId,
                transaction,
                cancellationToken)
            ? PreparationAuthorizationOutcome.Authorized
            : PreparationAuthorizationOutcome.Forbidden;
    }

    internal static async Task<bool> HasPreparationResponsibilityAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(transaction);
        command.CommandText =
            """
            SELECT 1
            FROM identities_and_capabilities.responsibility_assignments
            WHERE identity_id = @identity_id
              AND responsibility_code = 'Preparation'
            FOR SHARE
            """;
        AddParameter(command, "identity_id", identityId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    internal static async Task<bool> HasExactEnablementAsync(
        Guid identityId,
        Guid preparationResponsibilityId,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(transaction);
        command.CommandText =
            """
            SELECT 1
            FROM identities_and_capabilities.preparation_enablements
            WHERE identity_id = @identity_id
              AND preparation_responsibility_id = @preparation_responsibility_id
            FOR SHARE
            """;
        AddParameter(command, "identity_id", identityId);
        AddParameter(
            command,
            "preparation_responsibility_id",
            preparationResponsibilityId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    internal static DbCommand CreateCommand(DbTransaction transaction)
    {
        var connection = transaction.Connection
            ?? throw new InvalidOperationException(
                "The caller transaction must have an active connection.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "The caller transaction connection must be open.");
        }

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        return command;
    }

    internal static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

internal sealed class PreparationCapabilityStabilizer :
    IPreparationCapabilityStabilizer
{
    public Task<bool> StabilizePreparationResponsibilityAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        PreparationAuthorization.HasPreparationResponsibilityAsync(
            identityId,
            transaction,
            cancellationToken);

    public Task<bool> StabilizeExactEnablementAsync(
        Guid identityId,
        Guid preparationResponsibilityId,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        PreparationAuthorization.HasExactEnablementAsync(
            identityId,
            preparationResponsibilityId,
            transaction,
            cancellationToken);
}
