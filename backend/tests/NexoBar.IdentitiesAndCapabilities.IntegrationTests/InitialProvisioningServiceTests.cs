using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class InitialProvisioningServiceTests(
    IdentitiesAndCapabilitiesFixture fixture)
{
    [Fact]
    public async Task Fresh_installation_provisions_active_identity_credential_responsibility_and_fact()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        const string secret = "first secret";
        var recoveryFactor = Factor(1);
        var commandId = Guid.NewGuid();

        var result = await fixture.ProvisionInitialAsync(
            new InitialProvisioningRequest(
                commandId,
                "  Initial Administrator  ",
                "  initial-admin  ",
                secret,
                recoveryFactor),
            token);

        Assert.Equal(InitialProvisioningOutcome.Succeeded, result.Outcome);
        var identity = Assert.Single(await fixture.ReadIdentitiesAsync(token));
        Assert.Equal(result.IdentityId, identity.Id);
        Assert.Equal("Initial Administrator", identity.OperationalName);
        Assert.True(identity.IsActive);
        Assert.Equal(
            LoginOutcome.Succeeded,
            (await fixture.LoginAsync("initial-admin", secret, token)).Outcome);
        Assert.Equal(
            [FunctionalResponsibility.GeneralConfiguration],
            await fixture.ReadResponsibilitiesAsync(identity.Id, token));
        Assert.Equal(1, await fixture.CountInstallationProvisioningFactsAsync(token));
        var fact = await fixture.ReadInstallationProvisioningFactAsync(token);
        Assert.NotNull(fact);
        Assert.Equal(InstallationProvisioningOrigin.InitialProvisioning, fact.Origin);
        Assert.Equal(commandId, fact.ProvisioningCommandId);
        Assert.Equal(identity.Id, fact.InitialIdentityId);
        Assert.Equal(result.CompletedAt, fact.CompletedAt);
        Assert.NotNull(fact.RetryRecoveryFactorVerifier);
        Assert.NotEqual(recoveryFactor, fact.RetryRecoveryFactorVerifier);
        var state = await fixture.ReadInstallationRecoveryStateAsync(token);
        Assert.NotNull(state);
        Assert.Equal(InstallationRecoveryState.SingletonKey, state.Key);
        Assert.Equal(1, state.Generation);
        Assert.Equal(result.CompletedAt, state.EstablishedAt);
        Assert.Null(state.LastRotatedAt);
        Assert.NotEqual(recoveryFactor, state.RecoveryFactorVerifier);
        Assert.True(RecoveryFactor.TryParse(recoveryFactor, out var parsedFactor));
        var verifier = new PasswordRecoveryFactorVerifier();
        Assert.NotEqual(SecretVerificationResult.Failed,
            verifier.Verify(state.RecoveryFactorVerifier, parsedFactor!));
        Assert.NotEqual(SecretVerificationResult.Failed,
            verifier.Verify(fact.RetryRecoveryFactorVerifier!, parsedFactor!));
    }

    [Fact]
    public async Task Matching_command_and_intent_replays_original_success_without_mutation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var request = Request(Guid.NewGuid(), "Replay Administrator", "replay", "secret");

        var first = await fixture.ProvisionInitialAsync(request, token);
        var replay = await fixture.ProvisionInitialAsync(request, token);

        Assert.Equal(InitialProvisioningOutcome.Succeeded, first.Outcome);
        Assert.Equal(InitialProvisioningOutcome.ReplayedSuccess, replay.Outcome);
        Assert.Equal(first.IdentityId, replay.IdentityId);
        Assert.Equal(first.CommandId, replay.CommandId);
        Assert.Equal(first.CompletedAt, replay.CompletedAt);
        Assert.Single(await fixture.ReadIdentitiesAsync(token));
        Assert.Equal(1, await fixture.CountInstallationProvisioningFactsAsync(token));
        var state = await fixture.ReadInstallationRecoveryStateAsync(token);
        Assert.NotNull(state);
        Assert.Equal(first.CompletedAt, state.EstablishedAt);
        Assert.Equal(1, state.Generation);
        Assert.Null(state.LastRotatedAt);
    }

    [Theory]
    [InlineData("Changed Name", "replay", "secret")]
    [InlineData("Replay Administrator", "changed-login", "secret")]
    [InlineData("Replay Administrator", "replay", "changed-secret")]
    public async Task Matching_command_with_changed_intent_conflicts(
        string operationalName,
        string loginIdentifier,
        string secret)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var commandId = Guid.NewGuid();
        var original = Request(commandId, "Replay Administrator", "replay", "secret");

        await fixture.ProvisionInitialAsync(original, token);
        var conflict = await fixture.ProvisionInitialAsync(
            Request(commandId, operationalName, loginIdentifier, secret),
            token);

        Assert.Equal(InitialProvisioningOutcome.IntentConflict, conflict.Outcome);
        Assert.Single(await fixture.ReadIdentitiesAsync(token));
        Assert.Equal(1, await fixture.CountInstallationProvisioningFactsAsync(token));
    }

    [Fact]
    public async Task Matching_command_with_changed_recovery_factor_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var commandId = Guid.NewGuid();
        await fixture.ProvisionInitialAsync(
            Request(commandId, "Replay Administrator", "replay", "secret", Factor(1)), token);

        var conflict = await fixture.ProvisionInitialAsync(
            Request(commandId, "Replay Administrator", "replay", "secret", Factor(2)), token);

        Assert.Equal(InitialProvisioningOutcome.IntentConflict, conflict.Outcome);
    }

    [Fact]
    public async Task Independent_command_after_success_is_permanently_rejected()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await fixture.ProvisionInitialAsync(
            Request(Guid.NewGuid(), "Initial", "initial", "secret"), token);

        var result = await fixture.ProvisionInitialAsync(
            Request(Guid.NewGuid(), "Other", "other", "secret"), token);

        Assert.Equal(InitialProvisioningOutcome.AlreadyInitialized, result.Outcome);
        Assert.Single(await fixture.ReadIdentitiesAsync(token));
    }

    [Fact]
    public async Task Existing_login_conflict_leaves_no_partial_initialization()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var existing = await fixture.CreateIdentityAsync("Existing", true, token);
        await fixture.ProvisionCredentialAsync(existing.Id, "occupied", "old-secret", token);

        var result = await fixture.ProvisionInitialAsync(
            Request(Guid.NewGuid(), "Initial", "occupied", "secret"), token);

        Assert.Equal(InitialProvisioningOutcome.DuplicateLogin, result.Outcome);
        Assert.Equal([existing], await fixture.ReadIdentitiesAsync(token));
        Assert.Equal(0, await fixture.CountAssignmentsAsync(token));
        Assert.Equal(0, await fixture.CountInstallationProvisioningFactsAsync(token));
        Assert.Equal(0, await fixture.CountInstallationRecoveryStatesAsync(token));
    }

    [Fact]
    public async Task Failure_before_commit_leaves_no_partial_initialization()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var options = new DbContextOptionsBuilder<IdentitiesAndCapabilitiesDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new ThrowBeforeCommitInterceptor())
            .Options;
        await using var dbContext = new IdentitiesAndCapabilitiesDbContext(options);
        var service = new InitialProvisioningService(
            dbContext,
            new PasswordSecretVerifier(),
            new PasswordRecoveryFactorVerifier(),
            fixture.Clock,
            NullLogger<InitialProvisioningService>.Instance);

        var result = await service.ProvisionAsync(
            Request(Guid.NewGuid(), "Initial", "initial", "secret"), token);

        Assert.Equal(InitialProvisioningOutcome.InfrastructureFailure, result.Outcome);
        Assert.Empty(await fixture.ReadIdentitiesAsync(token));
        Assert.Equal(0, await fixture.CountAssignmentsAsync(token));
        Assert.Equal(0, await fixture.CountInstallationProvisioningFactsAsync(token));
        Assert.Equal(0, await fixture.CountInstallationRecoveryStatesAsync(token));
    }

    [Fact]
    public async Task Concurrent_independent_attempts_allow_exactly_one_commit()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        var results = await Task.WhenAll(
            fixture.ProvisionInitialAsync(
                Request(Guid.NewGuid(), "First", "first", "secret-one"), token),
            fixture.ProvisionInitialAsync(
                Request(Guid.NewGuid(), "Second", "second", "secret-two"), token));

        Assert.Equal(1, results.Count(result =>
            result.Outcome == InitialProvisioningOutcome.Succeeded));
        Assert.Equal(1, results.Count(result =>
            result.Outcome == InitialProvisioningOutcome.AlreadyInitialized));
        Assert.Single(await fixture.ReadIdentitiesAsync(token));
        Assert.Equal(1, await fixture.CountInstallationProvisioningFactsAsync(token));
        Assert.Equal(1, await fixture.CountInstallationRecoveryStatesAsync(token));
    }

    [Fact]
    public async Task Later_identity_deactivation_does_not_reopen_initial_provisioning()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var first = await fixture.ProvisionInitialAsync(
            Request(Guid.NewGuid(), "Initial", "initial", "secret"), token);
        await fixture.SetIdentityActiveAsync(first.IdentityId!.Value, false, token);

        var result = await fixture.ProvisionInitialAsync(
            Request(Guid.NewGuid(), "Replacement", "replacement", "secret"), token);

        Assert.Equal(InitialProvisioningOutcome.AlreadyInitialized, result.Outcome);
        Assert.Single(await fixture.ReadIdentitiesAsync(token));
    }

    [Fact]
    public async Task Recovery_state_without_provisioning_fact_fails_closed()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await fixture.InsertInstallationRecoveryStateAsync(
            new InstallationRecoveryState("persisted-recovery-verifier", 1, fixture.Clock.GetUtcNow()),
            token);

        var result = await fixture.ProvisionInitialAsync(
            Request(Guid.NewGuid(), "Initial", "initial", "secret"), token);

        Assert.Equal(InitialProvisioningOutcome.InfrastructureFailure, result.Outcome);
        Assert.Empty(await fixture.ReadIdentitiesAsync(token));
        Assert.Equal(0, await fixture.CountInstallationProvisioningFactsAsync(token));
        Assert.Equal(1, await fixture.CountInstallationRecoveryStatesAsync(token));
    }

    [Fact]
    public async Task Legacy_initial_provisioning_fact_never_replays_or_establishes_recovery()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var commandId = Guid.NewGuid();
        await InsertHistoricalInitialProvisioningFactAsync(commandId, token);

        var result = await fixture.ProvisionInitialAsync(
            Request(commandId, "Initial", "initial", "secret"), token);

        Assert.Equal(InitialProvisioningOutcome.AlreadyInitialized, result.Outcome);
        Assert.Equal(0, await fixture.CountInstallationRecoveryStatesAsync(token));
    }

    [Fact]
    public async Task Legacy_backfill_never_establishes_recovery()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await InsertLegacyBackfillFactAsync(token);

        var result = await fixture.ProvisionInitialAsync(
            Request(Guid.NewGuid(), "Initial", "initial", "secret"), token);

        Assert.Equal(InitialProvisioningOutcome.AlreadyInitialized, result.Outcome);
        Assert.Equal(0, await fixture.CountInstallationRecoveryStatesAsync(token));
    }

    [Fact]
    public async Task Replay_uses_historical_recovery_factor_after_current_factor_changes()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var commandId = Guid.NewGuid();
        var original = Request(commandId, "Initial", "initial", "secret", Factor(1));
        await fixture.ProvisionInitialAsync(original, token);
        Assert.True(RecoveryFactor.TryParse(Factor(2), out var rotatedFactor));
        var rotatedVerifier = new PasswordRecoveryFactorVerifier().Hash(rotatedFactor!);
        await ReplaceCurrentRecoveryVerifierAsync(rotatedVerifier, token);

        var replay = await fixture.ProvisionInitialAsync(original, token);
        var conflict = await fixture.ProvisionInitialAsync(
            Request(commandId, "Initial", "initial", "secret", Factor(2)), token);

        Assert.Equal(InitialProvisioningOutcome.ReplayedSuccess, replay.Outcome);
        Assert.Equal(InitialProvisioningOutcome.IntentConflict, conflict.Outcome);
        var state = await fixture.ReadInstallationRecoveryStateAsync(token);
        Assert.NotNull(state);
        Assert.Equal(rotatedVerifier, state.RecoveryFactorVerifier);
    }

    [Fact]
    public async Task Secrets_are_only_stored_as_verifiers_and_not_returned()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        const string secret = "raw bootstrap secret";

        var result = await fixture.ProvisionInitialAsync(
            Request(Guid.NewGuid(), "Initial", "initial", secret), token);
        var credential = await fixture.ReadCredentialAsync(result.IdentityId!.Value, token);
        var fact = await fixture.ReadInstallationProvisioningFactAsync(token);

        Assert.NotNull(credential);
        Assert.NotEqual(secret, credential.SecretVerifier);
        Assert.NotNull(fact);
        Assert.NotEqual(secret, fact.RetrySecretVerifier);
        Assert.NotNull(fact.RetryRecoveryFactorVerifier);
        Assert.NotEqual(Factor(0), fact.RetryRecoveryFactorVerifier);
        Assert.DoesNotContain(
            typeof(InitialProvisioningResult).GetProperties(),
            property => property.Name.Contains("Verifier", StringComparison.Ordinal) ||
                property.Name.Contains("Recovery", StringComparison.Ordinal));
    }

    private static InitialProvisioningRequest Request(
        Guid commandId,
        string operationalName,
        string loginIdentifier,
        string secret,
        string? recoveryFactor = null) =>
        new(commandId, operationalName, loginIdentifier, secret, recoveryFactor ?? Factor(0));

    private static string Factor(int seed) =>
        Convert.ToBase64String(Enumerable.Range(seed, 32).Select(value => (byte)value).ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private async Task InsertHistoricalInitialProvisioningFactAsync(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO identities_and_capabilities.installation_provisioning (
                singleton_key, origin, completed_at, provisioning_command_id,
                initial_identity_id, retry_intent_fingerprint, retry_secret_verifier)
            VALUES (1, 'InitialProvisioning', @completedAt, @commandId, @identityId,
                @fingerprint, 'historical-slow-verifier')
            """;
        command.Parameters.AddWithValue("completedAt", fixture.Clock.GetUtcNow());
        command.Parameters.AddWithValue("commandId", commandId);
        command.Parameters.AddWithValue("identityId", Guid.NewGuid());
        command.Parameters.AddWithValue("fingerprint", new byte[] { 1 });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertLegacyBackfillFactAsync(CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO identities_and_capabilities.installation_provisioning (singleton_key, origin)
            VALUES (1, 'LegacyBackfill')
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ReplaceCurrentRecoveryVerifierAsync(
        string verifier,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE identities_and_capabilities.installation_recovery_state
            SET recovery_factor_verifier = @verifier,
                generation = 2,
                last_rotated_at = @rotatedAt
            WHERE singleton_key = 1
            """;
        command.Parameters.AddWithValue("verifier", verifier);
        command.Parameters.AddWithValue("rotatedAt", fixture.Clock.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class ThrowBeforeCommitInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(
                new InvalidOperationException("Injected persistence failure."));
    }
}
