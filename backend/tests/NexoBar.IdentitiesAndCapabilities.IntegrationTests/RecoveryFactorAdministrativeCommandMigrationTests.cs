using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class RecoveryFactorAdministrativeCommandMigrationTests(
    IdentitiesAndCapabilitiesFixture fixture)
{
    private const string PreviousMigration =
        "20260920120000_AddInstallationRecoveryPersistence";
    private const string CurrentMigration =
        "20260920130000_AddRecoveryFactorAdministrativeCommand";

    [Fact]
    public async Task Migration_extends_only_recovery_factor_command_constraints_and_round_trips()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        try
        {
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(PreviousMigration, token);
            var actor = await fixture.CreateIdentityAsync("Migration Actor", true, token);
            await InsertCommandAsync(actor.Id, "CreateIdentity", null, token);

            await fixture.MigrateIdentitiesAndCapabilitiesAsync(CurrentMigration, token);
            await InsertCommandAsync(
                actor.Id,
                "RotateInstallationRecoveryFactor",
                "slow-recovery-verifier",
                token);
            await AssertConstraintViolationAsync(
                actor.Id,
                "RotateInstallationRecoveryFactor",
                null,
                token);
            await AssertConstraintViolationAsync(
                actor.Id,
                "CreateIdentity",
                "unexpected-verifier",
                token);

            await fixture.MigrateIdentitiesAndCapabilitiesAsync(PreviousMigration, token);
            await AssertConstraintViolationAsync(
                actor.Id,
                "RotateInstallationRecoveryFactor",
                "slow-recovery-verifier",
                token);
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(CurrentMigration, token);
        }
        finally
        {
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(CurrentMigration, token);
            await fixture.ResetAsync(token);
        }
    }

    [Fact]
    public async Task Model_has_no_pending_changes() =>
        Assert.False(await fixture.HasPendingModelChangesAsync());

    private async Task InsertCommandAsync(
        Guid actorIdentityId,
        string kind,
        string? verifier,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO identities_and_capabilities.administrative_commands (
                idempotency_key, actor_identity_id, command_kind, intent_fingerprint,
                intent_secret_verifier, result_payload)
            VALUES (@key, @actorIdentityId, @kind, @fingerprint, @verifier, '{}'::jsonb)
            """;
        command.Parameters.AddWithValue("key", Guid.NewGuid());
        command.Parameters.AddWithValue("actorIdentityId", actorIdentityId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("fingerprint", new byte[] { 1 });
        command.Parameters.AddWithValue("verifier", (object?)verifier ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task AssertConstraintViolationAsync(
        Guid actorIdentityId,
        string kind,
        string? verifier,
        CancellationToken cancellationToken)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertCommandAsync(actorIdentityId, kind, verifier, cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }
}
