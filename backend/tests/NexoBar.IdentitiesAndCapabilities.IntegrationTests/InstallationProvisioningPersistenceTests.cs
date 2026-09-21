using Microsoft.EntityFrameworkCore;
using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class InstallationProvisioningPersistenceTests(
    IdentitiesAndCapabilitiesFixture fixture)
{
    private const string PreviousMigration =
        "20260831190000_AddIdentityAdministrationCommands";
    private const string CurrentMigration =
        "20260919120000_AddInstallationProvisioningFact";
    private const string LatestMigration =
        "20260920120000_AddInstallationRecoveryPersistence";

    [Fact]
    public async Task Migration_creates_installation_provisioning_table()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        Assert.True(await InstallationProvisioningTableExistsAsync(token));
    }

    [Fact]
    public async Task Initial_provisioning_shaped_fact_is_persisted_and_singleton()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var commandId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var completedAt = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var fingerprint = new byte[] { 1, 2, 3 };

        await fixture.InsertInstallationProvisioningFactAsync(
            new InstallationProvisioningFact(
                commandId,
                identityId,
                completedAt,
                fingerprint,
                "slow-verifier"),
            token);

        var fact = await fixture.ReadInstallationProvisioningFactAsync(token);
        Assert.NotNull(fact);
        Assert.Equal(InstallationProvisioningFact.SingletonKey, fact.Key);
        Assert.Equal(InstallationProvisioningOrigin.InitialProvisioning, fact.Origin);
        Assert.Equal(completedAt, fact.CompletedAt);
        Assert.Equal(commandId, fact.ProvisioningCommandId);
        Assert.Equal(identityId, fact.InitialIdentityId);
        Assert.Equal(fingerprint, fact.RetryIntentFingerprint);
        Assert.Equal("slow-verifier", fact.RetrySecretVerifier);
        Assert.Null(fact.RetryRecoveryFactorVerifier);

        await using var connection = await fixture.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO identities_and_capabilities.installation_provisioning (
                singleton_key,
                origin)
            VALUES (1, 'LegacyBackfill')
            """;

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(token));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("PK_installation_provisioning", exception.ConstraintName);
    }

    [Fact]
    public async Task Existing_identity_is_conservatively_backfilled()
    {
        var fact = await MigrateExistingIdentityAsync("Legacy Identity", true, false);

        AssertLegacyBackfill(fact);
    }

    [Fact]
    public async Task Inactive_only_identity_is_conservatively_backfilled()
    {
        var fact = await MigrateExistingIdentityAsync("Inactive Legacy", false, false);

        AssertLegacyBackfill(fact);
    }

    [Fact]
    public async Task Identity_without_general_configuration_is_conservatively_backfilled()
    {
        var fact = await MigrateExistingIdentityAsync("No General Configuration", true, false);

        AssertLegacyBackfill(fact);
    }

    [Fact]
    public async Task Empty_pre_feature_database_receives_no_invented_fact()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        try
        {
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(PreviousMigration, token);
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(CurrentMigration, token);
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(LatestMigration, token);

            Assert.Null(await fixture.ReadInstallationProvisioningFactAsync(token));
        }
        finally
        {
            await RestoreLatestMigrationAsync(token);
        }
    }

    [Fact]
    public async Task Migration_down_removes_only_the_new_fact_table()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        try
        {
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(PreviousMigration, token);

            Assert.False(await InstallationProvisioningTableExistsAsync(token));
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

    private async Task<InstallationProvisioningFactSnapshot>
        MigrateExistingIdentityAsync(
            string operationalName,
            bool isActive,
            bool assignGeneralConfiguration)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        try
        {
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(PreviousMigration, token);
            var identity = await fixture.CreateIdentityAsync(operationalName, isActive, token);
            if (assignGeneralConfiguration)
            {
                await fixture.InsertAssignmentAsync(
                    identity.Id,
                    FunctionalResponsibility.GeneralConfiguration,
                    token);
            }

            await fixture.MigrateIdentitiesAndCapabilitiesAsync(CurrentMigration, token);
            await fixture.MigrateIdentitiesAndCapabilitiesAsync(LatestMigration, token);
            return Assert.IsType<InstallationProvisioningFactSnapshot>(
                await fixture.ReadInstallationProvisioningFactAsync(token));
        }
        finally
        {
            await RestoreLatestMigrationAsync(token);
        }
    }

    private async Task<bool> InstallationProvisioningTableExistsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT to_regclass(
                'identities_and_capabilities.installation_provisioning') IS NOT NULL
            """;
        return Assert.IsType<bool>(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task RestoreLatestMigrationAsync(CancellationToken cancellationToken)
    {
        await fixture.MigrateIdentitiesAndCapabilitiesAsync(LatestMigration, cancellationToken);
        await fixture.ResetAsync(cancellationToken);
    }

    private static void AssertLegacyBackfill(InstallationProvisioningFactSnapshot fact)
    {
        Assert.Equal(InstallationProvisioningFact.SingletonKey, fact.Key);
        Assert.Equal(InstallationProvisioningOrigin.LegacyBackfill, fact.Origin);
        Assert.Null(fact.CompletedAt);
        Assert.Null(fact.ProvisioningCommandId);
        Assert.Null(fact.InitialIdentityId);
        Assert.Null(fact.RetryIntentFingerprint);
        Assert.Null(fact.RetrySecretVerifier);
        Assert.Null(fact.RetryRecoveryFactorVerifier);
    }
}
