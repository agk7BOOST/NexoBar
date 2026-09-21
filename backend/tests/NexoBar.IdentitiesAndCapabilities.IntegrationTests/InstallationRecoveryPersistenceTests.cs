using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class InstallationRecoveryPersistenceTests(
    IdentitiesAndCapabilitiesFixture fixture)
{
    private const string PreviousMigration =
        "20260919120000_AddInstallationProvisioningFact";
    private const string CurrentMigration =
        "20260920120000_AddInstallationRecoveryPersistence";

    [Fact]
    public async Task Migration_creates_recovery_state_and_command_tables()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        Assert.True(await TableExistsAsync("installation_recovery_state", token));
        Assert.True(await TableExistsAsync(
            "extraordinary_general_configuration_recovery_commands",
            token));
    }

    [Fact]
    public async Task Existing_initial_provisioning_fact_remains_without_retry_recovery_verifier()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        try
        {
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(PreviousMigration, token);
            await InsertInitialProvisioningFactAsync(token);

            await fixture.MigrateIdentitiesAndCapabilitiesAsync(CurrentMigration, token);

            var fact = await fixture.ReadInstallationProvisioningFactAsync(token);
            Assert.NotNull(fact);
            Assert.Equal(InstallationProvisioningOrigin.InitialProvisioning, fact.Origin);
            Assert.Null(fact.RetryRecoveryFactorVerifier);
            Assert.Equal(0, await fixture.CountInstallationRecoveryStatesAsync(token));
        }
        finally
        {
            await RestoreLatestMigrationAsync(token);
        }
    }

    [Fact]
    public async Task Existing_legacy_backfill_fact_remains_without_retry_recovery_verifier()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        try
        {
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(PreviousMigration, token);
            await InsertLegacyBackfillFactAsync(token);

            await fixture.MigrateIdentitiesAndCapabilitiesAsync(CurrentMigration, token);

            var fact = await fixture.ReadInstallationProvisioningFactAsync(token);
            Assert.NotNull(fact);
            Assert.Equal(InstallationProvisioningOrigin.LegacyBackfill, fact.Origin);
            Assert.Null(fact.RetryRecoveryFactorVerifier);
            Assert.Equal(0, await fixture.CountInstallationRecoveryStatesAsync(token));
        }
        finally
        {
            await RestoreLatestMigrationAsync(token);
        }
    }

    [Fact]
    public async Task Empty_database_migration_does_not_create_recovery_state()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        try
        {
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(PreviousMigration, token);
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(CurrentMigration, token);

            Assert.Equal(0, await fixture.CountInstallationRecoveryStatesAsync(token));
        }
        finally
        {
            await RestoreLatestMigrationAsync(token);
        }
    }

    [Fact]
    public async Task Valid_recovery_state_persists_and_second_singleton_is_rejected()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var establishedAt = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await fixture.InsertInstallationRecoveryStateAsync(
            new InstallationRecoveryState("slow-recovery-verifier", 1, establishedAt),
            token);

        var state = await fixture.ReadInstallationRecoveryStateAsync(token);
        Assert.NotNull(state);
        Assert.Equal(InstallationRecoveryState.SingletonKey, state.Key);
        Assert.Equal("slow-recovery-verifier", state.RecoveryFactorVerifier);
        Assert.Equal(1, state.Generation);
        Assert.Equal(establishedAt, state.EstablishedAt);
        Assert.Null(state.LastRotatedAt);

        await using var connection = await fixture.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO identities_and_capabilities.installation_recovery_state (
                singleton_key,
                recovery_factor_verifier,
                generation,
                established_at)
            VALUES (1, 'another-verifier', 1, TIMESTAMPTZ '2030-01-01 12:00:00+00')
            """;
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(token));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("PK_installation_recovery_state", exception.ConstraintName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_recovery_state_generation_is_rejected(int generation)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await using var connection = await fixture.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO identities_and_capabilities.installation_recovery_state (
                singleton_key,
                recovery_factor_verifier,
                generation,
                established_at)
            VALUES (1, 'slow-recovery-verifier', {generation}, TIMESTAMPTZ '2030-01-01 12:00:00+00')
            """;

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_installation_recovery_state_generation", exception.ConstraintName);
    }

    [Fact]
    public async Task Recovery_state_rotation_before_establishment_is_rejected()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await using var connection = await fixture.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO identities_and_capabilities.installation_recovery_state (
                singleton_key,
                recovery_factor_verifier,
                generation,
                established_at,
                last_rotated_at)
            VALUES (
                1,
                'slow-recovery-verifier',
                2,
                TIMESTAMPTZ '2030-01-01 12:00:00+00',
                TIMESTAMPTZ '2030-01-01 11:59:59+00')
            """;

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_installation_recovery_state_rotation_time", exception.ConstraintName);
    }

    [Fact]
    public async Task Valid_extraordinary_recovery_command_persists_without_target_foreign_key()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var commandId = Guid.NewGuid();
        var targetIdentityId = Guid.NewGuid();
        var completedAt = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await fixture.InsertExtraordinaryRecoveryCommandAsync(
            new ExtraordinaryGeneralConfigurationRecoveryCommand(
                commandId,
                targetIdentityId,
                ExtraordinaryRecoveryLoginIntentMode.ExplicitIdentifier,
                "  Normalized-Login  ",
                "slow-credential-verifier",
                1,
                completedAt),
            token);

        var command = await fixture.ReadExtraordinaryRecoveryCommandAsync(token);
        Assert.NotNull(command);
        Assert.Equal(commandId, command.CommandId);
        Assert.Equal(targetIdentityId, command.TargetIdentityId);
        Assert.Equal(ExtraordinaryRecoveryLoginIntentMode.ExplicitIdentifier,
            command.LoginIntentMode);
        Assert.Equal("NORMALIZED-LOGIN", command.RequestedLoginIdentifier);
        Assert.Equal("slow-credential-verifier", command.RetryCredentialVerifier);
        Assert.Equal(1, command.RecoveryFactorGeneration);
        Assert.Equal(completedAt, command.CompletedAt);
    }

    [Theory]
    [InlineData("PreserveExisting", "unexpected-login")]
    [InlineData("ExplicitIdentifier", null)]
    public async Task Inconsistent_extraordinary_recovery_login_intent_is_rejected(
        string mode,
        string? requestedLoginIdentifier)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await using var connection = await fixture.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO identities_and_capabilities.extraordinary_general_configuration_recovery_commands (
                command_id,
                target_identity_id,
                login_intent_mode,
                requested_login_identifier,
                retry_credential_verifier,
                recovery_factor_generation,
                completed_at)
            VALUES (
                @commandId,
                @targetIdentityId,
                @mode,
                @requestedLoginIdentifier,
                'slow-credential-verifier',
                1,
                TIMESTAMPTZ '2030-01-01 12:00:00+00')
            """;
        command.Parameters.AddWithValue("commandId", Guid.NewGuid());
        command.Parameters.AddWithValue("targetIdentityId", Guid.NewGuid());
        command.Parameters.AddWithValue("mode", mode);
        command.Parameters.AddWithValue(
            "requestedLoginIdentifier",
            (object?)requestedLoginIdentifier ?? DBNull.Value);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(
            "CK_extraordinary_recovery_command_requested_login",
            exception.ConstraintName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_extraordinary_recovery_generation_is_rejected(
        int generation)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await using var connection = await fixture.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO identities_and_capabilities.extraordinary_general_configuration_recovery_commands (
                command_id,
                target_identity_id,
                login_intent_mode,
                retry_credential_verifier,
                recovery_factor_generation,
                completed_at)
            VALUES (
                '{Guid.NewGuid():D}',
                '{Guid.NewGuid():D}',
                'PreserveExisting',
                'slow-credential-verifier',
                {generation},
                TIMESTAMPTZ '2030-01-01 12:00:00+00')
            """;

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(
            "CK_extraordinary_recovery_command_generation",
            exception.ConstraintName);
    }

    [Fact]
    public async Task Down_removes_recovery_structures_and_retry_recovery_verifier_column()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        try
        {
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(PreviousMigration, token);

            Assert.False(await TableExistsAsync("installation_recovery_state", token));
            Assert.False(await TableExistsAsync(
                "extraordinary_general_configuration_recovery_commands",
                token));
            Assert.False(await InstallationProvisioningColumnExistsAsync(
                "retry_recovery_factor_verifier",
                token));

            await fixture.MigrateIdentitiesAndCapabilitiesAsync(CurrentMigration, token);
            Assert.True(await TableExistsAsync("installation_recovery_state", token));
            Assert.True(await InstallationProvisioningColumnExistsAsync(
                "retry_recovery_factor_verifier",
                token));
        }
        finally
        {
            await RestoreLatestMigrationAsync(token);
        }
    }

    [Fact]
    public async Task Model_has_no_pending_changes()
    {
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    private async Task InsertInitialProvisioningFactAsync(CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO identities_and_capabilities.installation_provisioning (
                singleton_key,
                origin,
                completed_at,
                provisioning_command_id,
                initial_identity_id,
                retry_intent_fingerprint,
                retry_secret_verifier)
            VALUES (
                1,
                'InitialProvisioning',
                TIMESTAMPTZ '2030-01-01 12:00:00+00',
                @commandId,
                @identityId,
                @fingerprint,
                'slow-credential-verifier')
            """;
        command.Parameters.AddWithValue("commandId", Guid.NewGuid());
        command.Parameters.AddWithValue("identityId", Guid.NewGuid());
        command.Parameters.AddWithValue("fingerprint", new byte[] { 1, 2, 3 });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertLegacyBackfillFactAsync(CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO identities_and_capabilities.installation_provisioning (
                singleton_key,
                origin)
            VALUES (1, 'LegacyBackfill')
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> TableExistsAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT to_regclass(@tableName) IS NOT NULL";
        command.Parameters.AddWithValue(
            "tableName",
            $"identities_and_capabilities.{tableName}");
        return Assert.IsType<bool>(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<bool> InstallationProvisioningColumnExistsAsync(
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'identities_and_capabilities'
                  AND table_name = 'installation_provisioning'
                  AND column_name = @columnName)
            """;
        command.Parameters.AddWithValue("columnName", columnName);
        return Assert.IsType<bool>(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task RestoreLatestMigrationAsync(CancellationToken cancellationToken)
    {
        await fixture.MigrateIdentitiesAndCapabilitiesAsync(CurrentMigration, cancellationToken);
        await fixture.ResetAsync(cancellationToken);
    }
}
