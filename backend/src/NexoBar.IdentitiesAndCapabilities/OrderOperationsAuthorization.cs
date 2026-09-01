using System.Data;
using System.Data.Common;

namespace NexoBar.IdentitiesAndCapabilities;

public interface IOrderOperationsAuthorization
{
    Task<OrderOperationsAuthorizationOutcome> AuthorizeAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken);
}

public interface IOrderOperationsCapabilityStabilizer
{
    Task<bool> StabilizeResponsibilityAsync(
        Guid identityId,
        DbTransaction transaction,
        CancellationToken cancellationToken);
}

public enum OrderOperationsAuthorizationOutcome
{
    Authorized,
    Unauthenticated,
    Forbidden
}

internal sealed class OrderOperationsAuthorization(
    IAuthenticatedSessionStabilizer sessionStabilizer) :
    IOrderOperationsAuthorization,
    IOrderOperationsCapabilityStabilizer
{
    public async Task<OrderOperationsAuthorizationOutcome> AuthorizeAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var session = await sessionStabilizer.StabilizeAsync(
            transaction,
            cancellationToken);
        if (session is null)
        {
            return OrderOperationsAuthorizationOutcome.Unauthenticated;
        }

        return await StabilizeResponsibilityAsync(
                session.IdentityId,
                transaction,
                cancellationToken)
            ? OrderOperationsAuthorizationOutcome.Authorized
            : OrderOperationsAuthorizationOutcome.Forbidden;
    }

    public async Task<bool> StabilizeResponsibilityAsync(
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
              AND responsibility_code = 'OrderOperationsAndBasicClosure'
            FOR SHARE
            """;
        AddParameter(command, "identity_id", identityId);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static DbCommand CreateCommand(DbTransaction transaction)
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

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
