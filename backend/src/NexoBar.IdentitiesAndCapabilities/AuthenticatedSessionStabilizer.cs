using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Options;

namespace NexoBar.IdentitiesAndCapabilities;

public interface IAuthenticatedSessionStabilizer
{
    Task<StabilizedAuthenticatedSession?> StabilizeAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken);
}

public sealed record StabilizedAuthenticatedSession(Guid IdentityId, Guid SessionId);

internal sealed class AuthenticatedSessionStabilizer(
    IAuthenticatedContext authenticatedContext,
    TimeProvider timeProvider,
    IOptions<ProvisionalSessionPolicyOptions> policyOptions) :
    IAuthenticatedSessionStabilizer
{
    private readonly ProvisionalSessionPolicyOptions policy = policyOptions.Value;

    public async Task<StabilizedAuthenticatedSession?> StabilizeAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (authenticatedContext.IdentityId is not { } identityId ||
            authenticatedContext.SessionId is not { } sessionId)
        {
            return null;
        }

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
            SELECT session_row.identity_id,
                   session_row.id,
                   session_row.last_activity_at,
                   session_row.absolute_expires_at,
                   session_row.revoked_at,
                   identity_row.is_active
            FROM identities_and_capabilities.sessions AS session_row
            INNER JOIN identities_and_capabilities.identities AS identity_row
                ON identity_row.id = session_row.identity_id
            WHERE session_row.id = @session_id
              AND session_row.identity_id = @identity_id
            FOR UPDATE OF session_row, identity_row
            """;
        AddParameter(command, "session_id", sessionId);
        AddParameter(command, "identity_id", identityId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var lastActivityAt = reader.GetFieldValue<DateTimeOffset>(2);
        var absoluteExpiresAt = reader.GetFieldValue<DateTimeOffset>(3);
        var revoked = !reader.IsDBNull(4);
        var identityIsActive = reader.GetBoolean(5);
        var now = SecurityTime.GetUtcNow(timeProvider);

        if (revoked ||
            !identityIsActive ||
            now >= absoluteExpiresAt ||
            now >= lastActivityAt + policy.InactivityTimeout)
        {
            return null;
        }

        return new StabilizedAuthenticatedSession(identityId, sessionId);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
