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
        var commandId = Guid.NewGuid();

        var result = await fixture.ProvisionInitialAsync(
            new InitialProvisioningRequest(
                commandId,
                "  Initial Administrator  ",
                "  initial-admin  ",
                secret),
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
            fixture.Clock,
            NullLogger<InitialProvisioningService>.Instance);

        var result = await service.ProvisionAsync(
            Request(Guid.NewGuid(), "Initial", "initial", "secret"), token);

        Assert.Equal(InitialProvisioningOutcome.InfrastructureFailure, result.Outcome);
        Assert.Empty(await fixture.ReadIdentitiesAsync(token));
        Assert.Equal(0, await fixture.CountAssignmentsAsync(token));
        Assert.Equal(0, await fixture.CountInstallationProvisioningFactsAsync(token));
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
        Assert.DoesNotContain(
            typeof(InitialProvisioningResult).GetProperties(),
            property => property.Name.Contains("Verifier", StringComparison.Ordinal));
    }

    private static InitialProvisioningRequest Request(
        Guid commandId,
        string operationalName,
        string loginIdentifier,
        string secret) =>
        new(commandId, operationalName, loginIdentifier, secret);

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
