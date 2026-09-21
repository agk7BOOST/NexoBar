using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class ExtraordinaryRecoveryConcurrencyAndAtomicityTests(
    IdentitiesAndCapabilitiesFixture fixture)
{
    [Fact]
    public async Task Independent_recoveries_commit_coherent_targets_and_distinct_durable_commands()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var factor = Factor(10);
        await EstablishRecoveryStateAsync(factor, token);
        var first = await fixture.CreateIdentityAsync("First recovery", false, token);
        var second = await fixture.CreateIdentityAsync("Second recovery", false, token);
        var now = fixture.Clock.GetUtcNow();
        var firstSession = new IdentitySession(
            first.Id, SessionToken.Generate().TokenHash, now, now.AddHours(1));
        var secondSession = new IdentitySession(
            second.Id, SessionToken.Generate().TokenHash, now, now.AddHours(1));
        await fixture.InsertSessionAsync(firstSession, token);
        await fixture.InsertSessionAsync(secondSession, token);

        var results = await Task.WhenAll(
            fixture.RecoverGeneralConfigurationAsync(
                Request(Guid.NewGuid(), first.Id, factor, "first.recovered", "first secret"), token),
            fixture.RecoverGeneralConfigurationAsync(
                Request(Guid.NewGuid(), second.Id, factor, "second.recovered", "second secret"), token));

        Assert.All(results, result => Assert.Equal(
            ExtraordinaryGeneralConfigurationRecoveryOutcome.Succeeded, result.Outcome));
        Assert.Equal(2, await fixture.CountExtraordinaryRecoveryCommandsAsync(token));
        var identities = await fixture.ReadIdentitiesAsync(token);
        Assert.All(identities, identity => Assert.True(identity.IsActive));
        foreach (var identityId in new[] { first.Id, second.Id })
        {
            Assert.Contains(
                FunctionalResponsibility.GeneralConfiguration,
                await fixture.ReadResponsibilitiesAsync(identityId, token));
        }
        Assert.Equal(LoginOutcome.Succeeded,
            (await fixture.LoginAsync("first.recovered", "first secret", token)).Outcome);
        Assert.Equal(LoginOutcome.Succeeded,
            (await fixture.LoginAsync("second.recovered", "second secret", token)).Outcome);
        var sessions = await fixture.ReadSessionsAsync(token);
        Assert.NotNull(sessions.Single(session => session.Id == firstSession.Id).RevokedAt);
        Assert.NotNull(sessions.Single(session => session.Id == secondSession.Id).RevokedAt);
    }

    [Fact]
    public async Task Recovery_then_rotation_are_serialized_by_the_shared_recovery_lock()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var firstFactor = Factor(11);
        var secondFactor = Factor(12);
        await EstablishRecoveryStateAsync(firstFactor, token);
        var target = await fixture.CreateIdentityAsync("Recovery first", false, token);
        var administrator = await CreateGeneralConfigurationIdentityAsync(token);
        var recoveryLock = new AdvisoryLockGate(pauseAfterAcquisition: true);
        var rotationLock = new AdvisoryLockGate(pauseAfterAcquisition: false);
        await using var recoveryContext = CreateContext(recoveryLock);
        await using var rotationContext = CreateContext(rotationLock);
        var recovery = CreateRecoveryService(recoveryContext);
        var rotation = CreateRotationService(rotationContext, administrator.Id);

        var recoveryTask = recovery.RecoverAsync(
            Request(Guid.NewGuid(), target.Id, firstFactor, "recovery.first", "new secret"), token);
        await recoveryLock.Acquired.Task.WaitAsync(token);
        var rotationTask = rotation.RotateAsync(
            Guid.NewGuid(), new InstallationRecoveryFactorRequest(secondFactor), token);
        await rotationLock.Attempted.Task.WaitAsync(token);
        Assert.False(rotationTask.IsCompleted);
        recoveryLock.Release();

        var recoveryResult = await recoveryTask;
        var rotationResult = await rotationTask;

        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.Succeeded, recoveryResult.Outcome);
        Assert.Equal(1, recoveryResult.RecoveryFactorGeneration);
        Assert.Equal(InstallationRecoveryFactorOutcome.Succeeded, rotationResult.Outcome);
        Assert.Equal(2, (await fixture.ReadInstallationRecoveryStateAsync(token))!.Generation);
    }

    [Fact]
    public async Task Rotation_then_recovery_rejects_the_old_factor_after_the_shared_lock_is_released()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var firstFactor = Factor(13);
        var secondFactor = Factor(14);
        await EstablishRecoveryStateAsync(firstFactor, token);
        var target = await fixture.CreateIdentityAsync("Rotation first", false, token);
        var administrator = await CreateGeneralConfigurationIdentityAsync(token);
        var rotationLock = new AdvisoryLockGate(pauseAfterAcquisition: true);
        var recoveryLock = new AdvisoryLockGate(pauseAfterAcquisition: false);
        await using var rotationContext = CreateContext(rotationLock);
        await using var recoveryContext = CreateContext(recoveryLock);
        var rotation = CreateRotationService(rotationContext, administrator.Id);
        var recovery = CreateRecoveryService(recoveryContext);

        var rotationTask = rotation.RotateAsync(
            Guid.NewGuid(), new InstallationRecoveryFactorRequest(secondFactor), token);
        await rotationLock.Acquired.Task.WaitAsync(token);
        var recoveryTask = recovery.RecoverAsync(
            Request(Guid.NewGuid(), target.Id, firstFactor, "must-not-apply", "new secret"), token);
        await recoveryLock.Attempted.Task.WaitAsync(token);
        Assert.False(recoveryTask.IsCompleted);
        rotationLock.Release();

        var rotationResult = await rotationTask;
        var recoveryResult = await recoveryTask;

        Assert.Equal(InstallationRecoveryFactorOutcome.Succeeded, rotationResult.Outcome);
        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.InvalidRecoveryFactor,
            recoveryResult.Outcome);
        Assert.False((await fixture.ReadIdentitiesAsync(token)).Single(
            identity => identity.Id == target.Id).IsActive);
        Assert.Null(await fixture.ReadCredentialAsync(target.Id, token));
        Assert.Empty(await fixture.ReadResponsibilitiesAsync(target.Id, token));
        Assert.Equal(0, await fixture.CountExtraordinaryRecoveryCommandsAsync(token));
    }

    [Fact]
    public async Task Failure_after_staging_recovery_mutations_rolls_back_every_target_change()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var factor = Factor(15);
        await EstablishRecoveryStateAsync(factor, token);
        var target = await fixture.CreateIdentityAsync("Rollback target", true, token);
        var originalCredential = await fixture.ProvisionCredentialAsync(
            target.Id, "rollback.login", "old secret", token);
        await fixture.InsertAssignmentAsync(target.Id, FunctionalResponsibility.Preparation, token);
        var enablement = Guid.NewGuid();
        await fixture.InsertEnablementAsync(target.Id, enablement, token);
        var now = fixture.Clock.GetUtcNow();
        var session = new IdentitySession(
            target.Id, SessionToken.Generate().TokenHash, now, now.AddHours(1));
        await fixture.InsertSessionAsync(session, token);
        await using var dbContext = CreateContext(new ThrowBeforeCommitInterceptor());
        var service = CreateRecoveryService(dbContext);

        var result = await service.RecoverAsync(
            Request(Guid.NewGuid(), target.Id, factor, "changed.login", "new secret"), token);

        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.InfrastructureFailure, result.Outcome);
        Assert.True((await fixture.ReadIdentitiesAsync(token)).Single(
            identity => identity.Id == target.Id).IsActive);
        Assert.Equal([FunctionalResponsibility.Preparation],
            await fixture.ReadResponsibilitiesAsync(target.Id, token));
        Assert.Equal([enablement], await fixture.ReadEnablementsAsync(target.Id, token));
        Assert.Equal(originalCredential, await fixture.ReadCredentialAsync(target.Id, token));
        Assert.Equal(LoginOutcome.Succeeded,
            (await fixture.LoginAsync("rollback.login", "old secret", token)).Outcome);
        Assert.Null((await fixture.ReadSessionsAsync(token)).Single(
            candidate => candidate.Id == session.Id).RevokedAt);
        Assert.Equal(0, await fixture.CountExtraordinaryRecoveryCommandsAsync(token));
    }

    [Fact]
    public async Task Recovery_and_ordinary_credential_replacement_serialize_on_the_target_identity_row()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var factor = Factor(16);
        await EstablishRecoveryStateAsync(factor, token);
        var target = await fixture.CreateIdentityAsync("Shared target", false, token);
        var administrator = await CreateGeneralConfigurationIdentityAsync(token);
        var recoveryRow = new IdentityRowGate(pauseAfterAcquisition: true);
        var administrationRow = new IdentityRowGate(pauseAfterAcquisition: false);
        await using var recoveryContext = CreateContext(recoveryRow);
        await using var administrationContext = CreateContext(administrationRow);
        var recovery = CreateRecoveryService(recoveryContext);
        var administration = new IdentityAdministrationService(
            administrationContext,
            new FixedSessionStabilizer(administrator.Id),
            new PasswordSecretVerifier(),
            null!,
            fixture.Clock);

        var recoveryTask = recovery.RecoverAsync(
            Request(Guid.NewGuid(), target.Id, factor, "recovered.login", "recovered secret"), token);
        await recoveryRow.Acquired.Task.WaitAsync(token);
        var administrationTask = administration.SetCredentialAsync(
            Guid.NewGuid(), target.Id,
            new SetLocalCredentialRequest("ordinary.login", "ordinary secret"), token);
        await administrationRow.Attempted.Task.WaitAsync(token);
        Assert.False(administrationTask.IsCompleted);
        recoveryRow.Release();

        var recoveryResult = await recoveryTask;
        var administrationResult = await administrationTask;

        Assert.Equal(ExtraordinaryGeneralConfigurationRecoveryOutcome.Succeeded, recoveryResult.Outcome);
        Assert.Equal(IdentityAdministrationOutcome.Succeeded, administrationResult.Outcome);
        Assert.Equal("ordinary.login", (await fixture.ReadCredentialAsync(target.Id, token))!.LoginIdentifier);
        Assert.Contains(FunctionalResponsibility.GeneralConfiguration,
            await fixture.ReadResponsibilitiesAsync(target.Id, token));
        Assert.True((await fixture.ReadIdentitiesAsync(token)).Single(
            identity => identity.Id == target.Id).IsActive);
    }

    private IdentitiesAndCapabilitiesDbContext CreateContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<IdentitiesAndCapabilitiesDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(interceptors)
            .Options);

    private ExtraordinaryGeneralConfigurationRecoveryService CreateRecoveryService(
        IdentitiesAndCapabilitiesDbContext dbContext) =>
        new(
            dbContext,
            new PasswordSecretVerifier(),
            new PasswordRecoveryFactorVerifier(),
            fixture.Clock,
            NullLogger<ExtraordinaryGeneralConfigurationRecoveryService>.Instance);

    private InstallationRecoveryFactorService CreateRotationService(
        IdentitiesAndCapabilitiesDbContext dbContext,
        Guid actorIdentityId) =>
        new(
            dbContext,
            new FixedSessionStabilizer(actorIdentityId),
            new PasswordRecoveryFactorVerifier(),
            fixture.Clock);

    private async Task<IdentitySnapshot> CreateGeneralConfigurationIdentityAsync(
        CancellationToken cancellationToken)
    {
        var identity = await fixture.CreateIdentityAsync("Rotation administrator", true, cancellationToken);
        await fixture.InsertAssignmentAsync(
            identity.Id, FunctionalResponsibility.GeneralConfiguration, cancellationToken);
        return identity;
    }

    private async Task EstablishRecoveryStateAsync(string factor, CancellationToken cancellationToken)
    {
        Assert.True(RecoveryFactor.TryParse(factor, out var parsed));
        await fixture.InsertInstallationRecoveryStateAsync(new InstallationRecoveryState(
            new PasswordRecoveryFactorVerifier().Hash(parsed!), 1, fixture.Clock.GetUtcNow()),
            cancellationToken);
    }

    private static ExtraordinaryGeneralConfigurationRecoveryRequest Request(
        Guid commandId, Guid targetIdentityId, string factor, string loginIdentifier, string secret) =>
        new(commandId, targetIdentityId, ExtraordinaryRecoveryLoginIntentMode.ExplicitIdentifier,
            loginIdentifier, factor, secret);

    private static string Factor(int seed) =>
        Convert.ToBase64String(Enumerable.Range(seed, 32).Select(value => (byte)value).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class FixedSessionStabilizer(Guid identityId) : IAuthenticatedSessionStabilizer
    {
        public Task<StabilizedAuthenticatedSession?> StabilizeAsync(
            DbTransaction transaction,
            CancellationToken cancellationToken) =>
            Task.FromResult<StabilizedAuthenticatedSession?>(
                new StabilizedAuthenticatedSession(identityId, Guid.NewGuid()));
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

    private sealed class AdvisoryLockGate(bool pauseAfterAcquisition) : DbCommandInterceptor
    {
        private TaskCompletionSource<bool> acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Acquired => acquired;

        internal TaskCompletionSource<bool> Attempted => attempted;

        internal void Release() => release.TrySetResult(true);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SignalAttempt(command);
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (IsRecoveryLock(command))
            {
                acquired.TrySetResult(true);
                if (pauseAfterAcquisition)
                {
                    await release.Task.WaitAsync(cancellationToken);
                }
            }

            return result;
        }

        private void SignalAttempt(DbCommand command)
        {
            if (IsRecoveryLock(command))
            {
                attempted.TrySetResult(true);
            }
        }

        private static bool IsRecoveryLock(DbCommand command) =>
            command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal);
    }

    private sealed class IdentityRowGate(bool pauseAfterAcquisition) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> acquired =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> attempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Acquired => acquired;

        internal TaskCompletionSource<bool> Attempted => attempted;

        internal void Release() => release.TrySetResult(true);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsIdentityUpdateLock(command))
            {
                attempted.TrySetResult(true);
            }

            return ValueTask.FromResult(result);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (IsIdentityUpdateLock(command))
            {
                acquired.TrySetResult(true);
                if (pauseAfterAcquisition)
                {
                    await release.Task.WaitAsync(cancellationToken);
                }
            }

            return result;
        }

        private static bool IsIdentityUpdateLock(DbCommand command) =>
            command.CommandText.Contains("identities_and_capabilities.identities", StringComparison.Ordinal) &&
            command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal);
    }
}
