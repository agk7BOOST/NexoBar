using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class ExtraordinaryGeneralConfigurationRecoveryService(
    IdentitiesAndCapabilitiesDbContext dbContext,
    ISecretVerifier secretVerifier,
    IRecoveryFactorVerifier recoveryFactorVerifier,
    TimeProvider timeProvider,
    ILogger<ExtraordinaryGeneralConfigurationRecoveryService> logger) :
    IExtraordinaryGeneralConfigurationRecoveryService
{
    public async Task<ExtraordinaryGeneralConfigurationRecoveryResult> RecoverAsync(
        ExtraordinaryGeneralConfigurationRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        var validated = Validate(request);
        if (validated is null)
        {
            logger.LogWarning(
                "Extraordinary GeneralConfiguration recovery rejected due to invalid input. CommandId: {CommandId} TargetIdentityId: {TargetIdentityId}",
                request.CommandId,
                request.TargetIdentityId);
            return ExtraordinaryGeneralConfigurationRecoveryResult.InvalidInput();
        }

        logger.LogInformation(
            "Extraordinary GeneralConfiguration recovery attempted. CommandId: {CommandId} TargetIdentityId: {TargetIdentityId}",
            request.CommandId,
            request.TargetIdentityId);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        try
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({InstallationRecoveryFactorService.RecoveryLockKey})",
                cancellationToken);

            var existing = await dbContext.ExtraordinaryGeneralConfigurationRecoveryCommands
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    command => command.CommandId == request.CommandId,
                    cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                if (!MatchesRetry(existing, validated))
                {
                    logger.LogWarning(
                        "Extraordinary GeneralConfiguration recovery intent conflicted. CommandId: {CommandId} TargetIdentityId: {TargetIdentityId}",
                        request.CommandId,
                        request.TargetIdentityId);
                    return ExtraordinaryGeneralConfigurationRecoveryResult.IntentConflict();
                }

                logger.LogInformation(
                    "Extraordinary GeneralConfiguration recovery replayed. CommandId: {CommandId} TargetIdentityId: {TargetIdentityId}",
                    existing.CommandId,
                    existing.TargetIdentityId);
                return ExtraordinaryGeneralConfigurationRecoveryResult.ReplayedSuccess(
                    existing.CommandId,
                    existing.TargetIdentityId,
                    existing.RecoveryFactorGeneration,
                    existing.CompletedAt);
            }

            var recoveryState = await dbContext.InstallationRecoveryStates
                .SingleOrDefaultAsync(cancellationToken);
            if (recoveryState is null)
            {
                logger.LogWarning(
                    "Extraordinary GeneralConfiguration recovery is not configured. CommandId: {CommandId} TargetIdentityId: {TargetIdentityId}",
                    request.CommandId,
                    request.TargetIdentityId);
                return ExtraordinaryGeneralConfigurationRecoveryResult.RecoveryNotConfigured();
            }

            if (recoveryFactorVerifier.Verify(
                    recoveryState.RecoveryFactorVerifier,
                    validated.RecoveryFactor) == SecretVerificationResult.Failed)
            {
                logger.LogWarning(
                    "Extraordinary GeneralConfiguration recovery rejected by recovery factor. CommandId: {CommandId} TargetIdentityId: {TargetIdentityId}",
                    request.CommandId,
                    request.TargetIdentityId);
                return ExtraordinaryGeneralConfigurationRecoveryResult.InvalidRecoveryFactor();
            }

            var identity = await FindIdentityForUpdateAsync(
                validated.TargetIdentityId,
                cancellationToken);
            if (identity is null)
            {
                logger.LogWarning(
                    "Extraordinary GeneralConfiguration recovery target was not found. CommandId: {CommandId} TargetIdentityId: {TargetIdentityId}",
                    request.CommandId,
                    request.TargetIdentityId);
                return ExtraordinaryGeneralConfigurationRecoveryResult.TargetNotFound();
            }

            var credential = await FindCredentialForUpdateAsync(
                identity.Id,
                cancellationToken);
            var login = ResolveLogin(validated, credential);
            if (login is null)
            {
                logger.LogWarning(
                    "Extraordinary GeneralConfiguration recovery rejected because its target has no preserved login. CommandId: {CommandId} TargetIdentityId: {TargetIdentityId}",
                    request.CommandId,
                    request.TargetIdentityId);
                return ExtraordinaryGeneralConfigurationRecoveryResult.InvalidInput();
            }

            var resolvedLogin = login.Value;

            if (await dbContext.LocalCredentials.AnyAsync(
                    candidate => candidate.NormalizedLoginIdentifier == resolvedLogin.Normalized &&
                        candidate.IdentityId != identity.Id,
                    cancellationToken))
            {
                logger.LogWarning(
                    "Extraordinary GeneralConfiguration recovery rejected because the login identifier is in use. CommandId: {CommandId} TargetIdentityId: {TargetIdentityId}",
                    request.CommandId,
                    request.TargetIdentityId);
                return ExtraordinaryGeneralConfigurationRecoveryResult.DuplicateLogin();
            }

            identity.Activate();
            if (!await dbContext.ResponsibilityAssignments.AnyAsync(
                    assignment => assignment.IdentityId == identity.Id &&
                        assignment.ResponsibilityCode ==
                            FunctionalResponsibility.GeneralConfiguration,
                    cancellationToken))
            {
                dbContext.ResponsibilityAssignments.Add(new ResponsibilityAssignment(
                    identity.Id,
                    FunctionalResponsibility.GeneralConfiguration));
            }

            var credentialVerifier = secretVerifier.Hash(validated.NewCredentialSecret);
            if (credential is null)
            {
                dbContext.LocalCredentials.Add(new LocalCredential(
                    identity.Id,
                    resolvedLogin.Identifier,
                    resolvedLogin.Normalized,
                    credentialVerifier));
            }
            else
            {
                credential.Replace(
                    resolvedLogin.Identifier,
                    resolvedLogin.Normalized,
                    credentialVerifier);
            }

            var completedAt = SecurityTime.GetUtcNow(timeProvider);
            await dbContext.Sessions
                .Where(session => session.IdentityId == identity.Id && session.RevokedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(session => session.RevokedAt, completedAt),
                    cancellationToken);
            dbContext.ExtraordinaryGeneralConfigurationRecoveryCommands.Add(
                new ExtraordinaryGeneralConfigurationRecoveryCommand(
                    validated.CommandId,
                    identity.Id,
                    validated.LoginIntentMode,
                    validated.RequestedLoginIdentifier,
                    secretVerifier.Hash(validated.NewCredentialSecret),
                    recoveryState.Generation,
                    completedAt));

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Extraordinary GeneralConfiguration recovery succeeded. CommandId: {CommandId} TargetIdentityId: {TargetIdentityId} RecoveryFactorGeneration: {RecoveryFactorGeneration}",
                validated.CommandId,
                identity.Id,
                recoveryState.Generation);
            return ExtraordinaryGeneralConfigurationRecoveryResult.Succeeded(
                validated.CommandId,
                identity.Id,
                recoveryState.Generation,
                completedAt);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "UX_local_credential_normalized_login"
            })
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogWarning(
                "Extraordinary GeneralConfiguration recovery rejected because the login identifier is in use. CommandId: {CommandId} TargetIdentityId: {TargetIdentityId}",
                request.CommandId,
                request.TargetIdentityId);
            return ExtraordinaryGeneralConfigurationRecoveryResult.DuplicateLogin();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogError(
                exception,
                "Extraordinary GeneralConfiguration recovery failed due to infrastructure. CommandId: {CommandId} TargetIdentityId: {TargetIdentityId}",
                request.CommandId,
                request.TargetIdentityId);
            return ExtraordinaryGeneralConfigurationRecoveryResult.InfrastructureFailure();
        }
    }

    private bool MatchesRetry(
        ExtraordinaryGeneralConfigurationRecoveryCommand command,
        ValidatedRecovery request) =>
        command.TargetIdentityId == request.TargetIdentityId &&
            command.LoginIntentMode == request.LoginIntentMode &&
        string.Equals(
            command.RequestedLoginIdentifier,
            request.NormalizedRequestedLoginIdentifier,
            StringComparison.Ordinal) &&
        secretVerifier.Verify(
            command.RetryCredentialVerifier,
            request.NewCredentialSecret) != SecretVerificationResult.Failed;

    private static (string Identifier, string Normalized)? ResolveLogin(
        ValidatedRecovery request,
        LocalCredential? credential)
    {
        if (request.LoginIntentMode == ExtraordinaryRecoveryLoginIntentMode.PreserveExisting)
        {
            return credential is null
                ? null
                : (credential.LoginIdentifier, credential.NormalizedLoginIdentifier);
        }

        return (request.RequestedLoginIdentifier!, request.NormalizedRequestedLoginIdentifier!);
    }

    private Task<Identity?> FindIdentityForUpdateAsync(
        Guid identityId,
        CancellationToken cancellationToken) =>
        dbContext.Identities.FromSqlInterpolated(
            $"SELECT * FROM identities_and_capabilities.identities WHERE id = {identityId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private Task<LocalCredential?> FindCredentialForUpdateAsync(
        Guid identityId,
        CancellationToken cancellationToken) =>
        dbContext.LocalCredentials.FromSqlInterpolated(
            $"SELECT * FROM identities_and_capabilities.local_credentials WHERE identity_id = {identityId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private static ValidatedRecovery? Validate(
        ExtraordinaryGeneralConfigurationRecoveryRequest request)
    {
        if (!IsUuidVersion4(request.CommandId) || request.TargetIdentityId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.NewCredentialSecret) ||
            !RecoveryFactor.TryParse(request.RecoveryFactor, out var recoveryFactor) ||
            !Enum.IsDefined(request.LoginIntentMode))
        {
            return null;
        }

        if (request.LoginIntentMode == ExtraordinaryRecoveryLoginIntentMode.PreserveExisting)
        {
            return request.RequestedLoginIdentifier is null
                ? new ValidatedRecovery(
                    request.CommandId,
                    request.TargetIdentityId,
                    request.LoginIntentMode,
                    null,
                    null,
                    request.NewCredentialSecret,
                    recoveryFactor!)
                : null;
        }

        var loginIdentifier = LoginIdentifierNormalizer.Trim(request.RequestedLoginIdentifier);
        var normalizedLoginIdentifier = LoginIdentifierNormalizer.Normalize(loginIdentifier);
        return loginIdentifier is null || normalizedLoginIdentifier is null
            ? null
            : new ValidatedRecovery(
                request.CommandId,
                request.TargetIdentityId,
                request.LoginIntentMode,
                loginIdentifier,
                normalizedLoginIdentifier,
                request.NewCredentialSecret,
                recoveryFactor!);
    }

    private static bool IsUuidVersion4(Guid value) =>
        value != Guid.Empty && (value.ToByteArray(bigEndian: true)[6] >> 4) == 4;

    private sealed record ValidatedRecovery(
        Guid CommandId,
        Guid TargetIdentityId,
        ExtraordinaryRecoveryLoginIntentMode LoginIntentMode,
        string? RequestedLoginIdentifier,
        string? NormalizedRequestedLoginIdentifier,
        string NewCredentialSecret,
        RecoveryFactor RecoveryFactor);
}

public interface IExtraordinaryGeneralConfigurationRecoveryService
{
    Task<ExtraordinaryGeneralConfigurationRecoveryResult> RecoverAsync(
        ExtraordinaryGeneralConfigurationRecoveryRequest request,
        CancellationToken cancellationToken);
}

public sealed record ExtraordinaryGeneralConfigurationRecoveryRequest(
    Guid CommandId,
    Guid TargetIdentityId,
    ExtraordinaryRecoveryLoginIntentMode LoginIntentMode,
    string? RequestedLoginIdentifier,
    string? RecoveryFactor,
    string? NewCredentialSecret);

public sealed record ExtraordinaryGeneralConfigurationRecoveryResult(
    ExtraordinaryGeneralConfigurationRecoveryOutcome Outcome,
    Guid? CommandId,
    Guid? TargetIdentityId,
    int? RecoveryFactorGeneration,
    DateTimeOffset? CompletedAt)
{
    internal static ExtraordinaryGeneralConfigurationRecoveryResult Succeeded(
        Guid commandId, Guid targetIdentityId, int recoveryFactorGeneration,
        DateTimeOffset completedAt) =>
        new(ExtraordinaryGeneralConfigurationRecoveryOutcome.Succeeded, commandId,
            targetIdentityId, recoveryFactorGeneration, completedAt);

    internal static ExtraordinaryGeneralConfigurationRecoveryResult ReplayedSuccess(
        Guid commandId, Guid targetIdentityId, int recoveryFactorGeneration,
        DateTimeOffset completedAt) =>
        new(ExtraordinaryGeneralConfigurationRecoveryOutcome.ReplayedSuccess, commandId,
            targetIdentityId, recoveryFactorGeneration, completedAt);

    internal static ExtraordinaryGeneralConfigurationRecoveryResult InvalidInput() =>
        new(ExtraordinaryGeneralConfigurationRecoveryOutcome.InvalidInput, null, null, null, null);

    internal static ExtraordinaryGeneralConfigurationRecoveryResult InvalidRecoveryFactor() =>
        new(ExtraordinaryGeneralConfigurationRecoveryOutcome.InvalidRecoveryFactor, null, null, null, null);

    internal static ExtraordinaryGeneralConfigurationRecoveryResult TargetNotFound() =>
        new(ExtraordinaryGeneralConfigurationRecoveryOutcome.TargetNotFound, null, null, null, null);

    internal static ExtraordinaryGeneralConfigurationRecoveryResult DuplicateLogin() =>
        new(ExtraordinaryGeneralConfigurationRecoveryOutcome.DuplicateLogin, null, null, null, null);

    internal static ExtraordinaryGeneralConfigurationRecoveryResult IntentConflict() =>
        new(ExtraordinaryGeneralConfigurationRecoveryOutcome.IntentConflict, null, null, null, null);

    internal static ExtraordinaryGeneralConfigurationRecoveryResult RecoveryNotConfigured() =>
        new(ExtraordinaryGeneralConfigurationRecoveryOutcome.RecoveryNotConfigured, null, null, null, null);

    internal static ExtraordinaryGeneralConfigurationRecoveryResult InfrastructureFailure() =>
        new(ExtraordinaryGeneralConfigurationRecoveryOutcome.InfrastructureFailure, null, null, null, null);
}

public enum ExtraordinaryGeneralConfigurationRecoveryOutcome
{
    Succeeded,
    ReplayedSuccess,
    InvalidInput,
    InvalidRecoveryFactor,
    TargetNotFound,
    DuplicateLogin,
    IntentConflict,
    RecoveryNotConfigured,
    InfrastructureFailure
}
