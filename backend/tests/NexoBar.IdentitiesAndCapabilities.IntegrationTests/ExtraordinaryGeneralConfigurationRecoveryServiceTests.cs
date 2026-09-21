using NexoBar.IdentitiesAndCapabilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class ExtraordinaryGeneralConfigurationRecoveryServiceTests(
    IdentitiesAndCapabilitiesFixture fixture)
{
    [Fact]
    public async Task Valid_factor_recovers_inactive_credentialless_target_without_touching_unrelated_state()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var factor = Factor(1);
        await EstablishRecoveryStateAsync(factor, token);
        var target = await fixture.CreateIdentityAsync("Recovery target", false, token);
        await fixture.InsertAssignmentAsync(
            target.Id, FunctionalResponsibility.Preparation, token);
        var enablement = Guid.NewGuid();
        await fixture.InsertEnablementAsync(target.Id, enablement, token);
        var now = fixture.Clock.GetUtcNow();
        var oldSession = new IdentitySession(
            target.Id, SessionToken.Generate().TokenHash, now, now.AddHours(1));
        await fixture.InsertSessionAsync(oldSession, token);

        var result = await fixture.RecoverGeneralConfigurationAsync(
            Request(Guid.NewGuid(), target.Id, factor, "recovered", "new secret"), token);

        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.Succeeded, result.Outcome);
        var identity = Assert.Single(await fixture.ReadIdentitiesAsync(token));
        Assert.True(identity.IsActive);
        Assert.Equal(
            [FunctionalResponsibility.GeneralConfiguration, FunctionalResponsibility.Preparation],
            await fixture.ReadResponsibilitiesAsync(target.Id, token));
        Assert.Equal([enablement], await fixture.ReadEnablementsAsync(target.Id, token));
        Assert.Equal(LoginOutcome.Succeeded, (await fixture.LoginAsync("recovered", "new secret", token)).Outcome);
        Assert.NotNull((await fixture.ReadSessionsAsync(token)).Single(
            session => session.Id == oldSession.Id).RevokedAt);
        var command = Assert.IsType<ExtraordinaryRecoveryCommandSnapshot>(
            await fixture.ReadExtraordinaryRecoveryCommandAsync(token));
        Assert.Equal(1, command.RecoveryFactorGeneration);
        Assert.NotEqual("new secret", command.RetryCredentialVerifier);
        Assert.Equal(SecretVerificationResult.Success,
            new PasswordSecretVerifier().Verify(command.RetryCredentialVerifier, "new secret"));
        Assert.DoesNotContain(typeof(ExtraordinaryGeneralConfigurationRecoveryResult).GetProperties(),
            property => property.Name.Contains("Secret", StringComparison.Ordinal) ||
                property.Name.Contains("Verifier", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(ExtraordinaryGeneralConfigurationRecoveryCommand).GetProperties(
                BindingFlags.Instance | BindingFlags.NonPublic),
            property => property.Name == "ActorIdentityId");
    }

    [Fact]
    public async Task Preserve_existing_login_replaces_secret_and_exact_replay_is_read_only()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var factor = Factor(2);
        await EstablishRecoveryStateAsync(factor, token);
        var target = await fixture.CreateIdentityAsync("Credentialed", false, token);
        await fixture.ProvisionCredentialAsync(target.Id, "Existing.Login", "old secret", token);
        var commandId = Guid.NewGuid();
        var request = new ExtraordinaryGeneralConfigurationRecoveryRequest(
            commandId, target.Id, ExtraordinaryRecoveryLoginIntentMode.PreserveExisting,
            null, factor, "new secret");

        var first = await fixture.RecoverGeneralConfigurationAsync(request, token);

        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.Succeeded, first.Outcome);
        Assert.Equal("Existing.Login", (await fixture.ReadCredentialAsync(target.Id, token))!.LoginIdentifier);
        Assert.Equal(LoginOutcome.InvalidCredentials, (await fixture.LoginAsync("Existing.Login", "old secret", token)).Outcome);
        Assert.Equal(LoginOutcome.Succeeded, (await fixture.LoginAsync("Existing.Login", "new secret", token)).Outcome);
        var verifierBeforeReplay = await fixture.ReadCredentialVerifierAsync(target.Id, token);
        var responsibilitiesBeforeReplay = await fixture.ReadResponsibilitiesAsync(target.Id, token);
        var sessionsBeforeReplay = await fixture.ReadSessionsAsync(token);
        await fixture.SetIdentityActiveAsync(target.Id, false, token);
        var replay = await fixture.RecoverGeneralConfigurationAsync(request, token);

        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.ReplayedSuccess, replay.Outcome);
        Assert.False((await fixture.ReadIdentitiesAsync(token)).Single().IsActive);
        Assert.Equal(verifierBeforeReplay, await fixture.ReadCredentialVerifierAsync(target.Id, token));
        Assert.Equal(responsibilitiesBeforeReplay, await fixture.ReadResponsibilitiesAsync(target.Id, token));
        Assert.Equal(
            sessionsBeforeReplay.Select(session => (session.Id, session.RevokedAt)),
            (await fixture.ReadSessionsAsync(token)).Select(session => (session.Id, session.RevokedAt)));
    }

    [Fact]
    public async Task Explicit_identifier_replaces_an_existing_login_and_secret()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var factor = Factor(9);
        await EstablishRecoveryStateAsync(factor, token);
        var target = await fixture.CreateIdentityAsync("Explicit replacement", false, token);
        await fixture.ProvisionCredentialAsync(target.Id, "old.login", "old secret", token);

        var result = await fixture.RecoverGeneralConfigurationAsync(
            Request(Guid.NewGuid(), target.Id, factor, "  New.Login  ", "new secret"), token);

        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.Succeeded, result.Outcome);
        var credential = Assert.IsType<CredentialSnapshot>(
            await fixture.ReadCredentialAsync(target.Id, token));
        Assert.Equal("New.Login", credential.LoginIdentifier);
        Assert.Equal("NEW.LOGIN", credential.NormalizedLoginIdentifier);
        Assert.Equal(LoginOutcome.InvalidCredentials, (await fixture.LoginAsync("old.login", "old secret", token)).Outcome);
        Assert.Equal(LoginOutcome.Succeeded, (await fixture.LoginAsync("new.login", "new secret", token)).Outcome);
    }

    [Fact]
    public async Task Invalid_factor_missing_state_and_duplicate_login_leave_target_unchanged()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await fixture.CreateIdentityAsync("Target", false, token);
        var missing = await fixture.RecoverGeneralConfigurationAsync(
            Request(Guid.NewGuid(), target.Id, Factor(3), "target", "new secret"), token);
        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.RecoveryNotConfigured, missing.Outcome);

        var factor = Factor(3);
        await EstablishRecoveryStateAsync(factor, token);
        var invalid = await fixture.RecoverGeneralConfigurationAsync(
            Request(Guid.NewGuid(), target.Id, Factor(4), "target", "new secret"), token);
        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.InvalidRecoveryFactor, invalid.Outcome);

        var owner = await fixture.CreateIdentityAsync("Owner", true, token);
        await fixture.ProvisionCredentialAsync(owner.Id, "taken", "secret", token);
        var duplicate = await fixture.RecoverGeneralConfigurationAsync(
            Request(Guid.NewGuid(), target.Id, factor, "TAKEN", "new secret"), token);
        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.DuplicateLogin, duplicate.Outcome);
        Assert.False((await fixture.ReadIdentitiesAsync(token)).Single(identity => identity.Id == target.Id).IsActive);
        Assert.Null(await fixture.ReadCredentialAsync(target.Id, token));
        Assert.Empty(await fixture.ReadResponsibilitiesAsync(target.Id, token));
    }

    [Fact]
    public async Task Credentialless_preserve_existing_is_invalid_and_conflicting_intents_do_not_mutate()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var factor = Factor(5);
        await EstablishRecoveryStateAsync(factor, token);
        var target = await fixture.CreateIdentityAsync("No inferred login", false, token);
        var invalid = await fixture.RecoverGeneralConfigurationAsync(
            new ExtraordinaryGeneralConfigurationRecoveryRequest(
                Guid.NewGuid(), target.Id, ExtraordinaryRecoveryLoginIntentMode.PreserveExisting,
                null, factor, "new secret"), token);
        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.InvalidInput, invalid.Outcome);
        Assert.Null(await fixture.ReadCredentialAsync(target.Id, token));

        var commandId = Guid.NewGuid();
        await fixture.RecoverGeneralConfigurationAsync(
            Request(commandId, target.Id, factor, "specific-login", "new secret"), token);
        var conflict = await fixture.RecoverGeneralConfigurationAsync(
            Request(commandId, target.Id, factor, "other-login", "new secret"), token);
        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.IntentConflict, conflict.Outcome);
        Assert.Equal("specific-login", (await fixture.ReadCredentialAsync(target.Id, token))!.LoginIdentifier);

        var anotherTarget = await fixture.CreateIdentityAsync("Another target", false, token);
        var differentTarget = await fixture.RecoverGeneralConfigurationAsync(
            Request(commandId, anotherTarget.Id, factor, "specific-login", "new secret"), token);
        var differentSecret = await fixture.RecoverGeneralConfigurationAsync(
            Request(commandId, target.Id, factor, "specific-login", "changed secret"), token);
        var differentMode = await fixture.RecoverGeneralConfigurationAsync(
            new ExtraordinaryGeneralConfigurationRecoveryRequest(
                commandId, target.Id, ExtraordinaryRecoveryLoginIntentMode.PreserveExisting,
                null, factor, "new secret"), token);
        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.IntentConflict, differentTarget.Outcome);
        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.IntentConflict, differentSecret.Outcome);
        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.IntentConflict, differentMode.Outcome);
    }

    [Fact]
    public async Task Replay_after_rotation_uses_original_result_without_validating_current_factor()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var firstFactor = Factor(6);
        await EstablishRecoveryStateAsync(firstFactor, token);
        var target = await fixture.CreateIdentityAsync("Replay target", false, token);
        var commandId = Guid.NewGuid();
        var request = Request(commandId, target.Id, firstFactor, "replay", "new secret");
        var succeeded = await fixture.RecoverGeneralConfigurationAsync(request, token);

        var currentFactor = Factor(7);
        await RotateRecoveryStateAsync(currentFactor, token);
        await fixture.SetIdentityActiveAsync(target.Id, false, token);
        var replay = await fixture.RecoverGeneralConfigurationAsync(request, token);
        var newCommandWithOldFactor = await fixture.RecoverGeneralConfigurationAsync(
            Request(Guid.NewGuid(), target.Id, firstFactor, "replay", "another secret"), token);

        Assert.Equal(1, succeeded.RecoveryFactorGeneration);
        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.ReplayedSuccess, replay.Outcome);
        Assert.Equal(1, replay.RecoveryFactorGeneration);
        Assert.Equal(2, (await fixture.ReadInstallationRecoveryStateAsync(token))!.Generation);
        Assert.False((await fixture.ReadIdentitiesAsync(token)).Single().IsActive);
        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.InvalidRecoveryFactor,
            newCommandWithOldFactor.Outcome);
        var newCommandWithCurrentFactor = await fixture.RecoverGeneralConfigurationAsync(
            Request(Guid.NewGuid(), target.Id, currentFactor, "replay", "current secret"), token);
        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.Succeeded,
            newCommandWithCurrentFactor.Outcome);
    }

    [Fact]
    public async Task Concurrent_same_command_attempts_commit_once_then_replay_under_the_shared_recovery_lock()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var factor = Factor(8);
        await EstablishRecoveryStateAsync(factor, token);
        var target = await fixture.CreateIdentityAsync("Concurrent target", false, token);
        var request = Request(Guid.NewGuid(), target.Id, factor, "concurrent", "new secret");

        var results = await Task.WhenAll(
            fixture.RecoverGeneralConfigurationAsync(request, token),
            fixture.RecoverGeneralConfigurationAsync(request, token));

        Assert.Contains(results, result =>
            result.Outcome == ExtraordinaryGeneralConfigurationRecoveryOutcome.Succeeded);
        Assert.Contains(results, result =>
            result.Outcome == ExtraordinaryGeneralConfigurationRecoveryOutcome.ReplayedSuccess);
        Assert.Equal(request.CommandId,
            Assert.IsType<ExtraordinaryRecoveryCommandSnapshot>(
                await fixture.ReadExtraordinaryRecoveryCommandAsync(token)).CommandId);
        Assert.Equal(1, (await fixture.ReadResponsibilitiesAsync(target.Id, token)).Count(
            responsibility => responsibility == FunctionalResponsibility.GeneralConfiguration));
    }

    private async Task EstablishRecoveryStateAsync(string factor, CancellationToken cancellationToken)
    {
        Assert.True(RecoveryFactor.TryParse(factor, out var parsed));
        await fixture.InsertInstallationRecoveryStateAsync(new InstallationRecoveryState(
            new PasswordRecoveryFactorVerifier().Hash(parsed!), 1, fixture.Clock.GetUtcNow()),
            cancellationToken);
    }

    private async Task RotateRecoveryStateAsync(string factor, CancellationToken cancellationToken)
    {
        Assert.True(RecoveryFactor.TryParse(factor, out var parsed));
        await using var scope = fixture.Services.CreateAsyncScope();
        var state = await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .InstallationRecoveryStates.SingleAsync(cancellationToken);
        state.Rotate(new PasswordRecoveryFactorVerifier().Hash(parsed!), 2, fixture.Clock.GetUtcNow());
        await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .SaveChangesAsync(cancellationToken);
    }

    private static ExtraordinaryGeneralConfigurationRecoveryRequest Request(
        Guid commandId, Guid targetIdentityId, string factor, string loginIdentifier, string secret) =>
        new(commandId, targetIdentityId, ExtraordinaryRecoveryLoginIntentMode.ExplicitIdentifier,
            loginIdentifier, factor, secret);

    private static string Factor(int seed) =>
        Convert.ToBase64String(Enumerable.Range(seed, 32).Select(value => (byte)value).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
