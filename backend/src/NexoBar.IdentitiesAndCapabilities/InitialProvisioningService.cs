using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class InitialProvisioningService(
    IdentitiesAndCapabilitiesDbContext dbContext,
    ISecretVerifier secretVerifier,
    IRecoveryFactorVerifier recoveryFactorVerifier,
    TimeProvider timeProvider,
    ILogger<InitialProvisioningService> logger) : IInitialProvisioningService
{
    private const long ProvisioningLockKey = 0x494350524F560000;

    public async Task<InitialProvisioningResult> ProvisionAsync(
        InitialProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        var validated = Validate(request);
        if (validated is null)
        {
            logger.LogWarning(
                "Initial provisioning rejected because its input is invalid. CommandId: {CommandId}",
                request.CommandId);
            return InitialProvisioningResult.InvalidInput();
        }

        logger.LogInformation(
            "Initial provisioning attempted. CommandId: {CommandId}",
            request.CommandId);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        try
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({ProvisioningLockKey})",
                cancellationToken);

            var fact = await dbContext.InstallationProvisioningFacts
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
            if (fact is not null)
            {
                if (fact.Origin != InstallationProvisioningOrigin.InitialProvisioning ||
                    fact.RetryRecoveryFactorVerifier is null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    logger.LogWarning(
                        "Initial provisioning rejected because the installation is already initialized. CommandId: {CommandId}",
                        request.CommandId);
                    return InitialProvisioningResult.AlreadyInitialized();
                }

                var replay = IsMatchingRetry(fact, request.CommandId, validated);
                await transaction.CommitAsync(cancellationToken);
                if (replay)
                {
                    logger.LogInformation(
                        "Initial provisioning replayed. CommandId: {CommandId} IdentityId: {IdentityId}",
                        request.CommandId,
                        fact.InitialIdentityId);
                    return InitialProvisioningResult.ReplayedSuccess(
                        fact.InitialIdentityId!.Value,
                        fact.ProvisioningCommandId!.Value,
                        fact.CompletedAt!.Value);
                }

                if (fact.Origin == InstallationProvisioningOrigin.InitialProvisioning &&
                    fact.ProvisioningCommandId == request.CommandId)
                {
                    logger.LogWarning(
                        "Initial provisioning retry conflicted with its original intent. CommandId: {CommandId}",
                        request.CommandId);
                    return InitialProvisioningResult.IntentConflict();
                }

                logger.LogWarning(
                    "Initial provisioning rejected because the installation is already initialized. CommandId: {CommandId}",
                    request.CommandId);
                return InitialProvisioningResult.AlreadyInitialized();
            }

            if (await dbContext.InstallationRecoveryStates.AnyAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.LogError(
                    "Initial provisioning encountered recovery state without a provisioning fact. CommandId: {CommandId}",
                    request.CommandId);
                return InitialProvisioningResult.InfrastructureFailure();
            }

            if (await dbContext.LocalCredentials.AnyAsync(
                    credential => credential.NormalizedLoginIdentifier ==
                        validated.NormalizedLoginIdentifier,
                    cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.LogWarning(
                    "Initial provisioning rejected because the login identifier is already in use. CommandId: {CommandId}",
                    request.CommandId);
                return InitialProvisioningResult.DuplicateLogin();
            }

            var identity = new Identity(validated.OperationalName, isActive: true);
            var credentialVerifier = secretVerifier.Hash(validated.Secret);
            var recoveryVerifier = recoveryFactorVerifier.Hash(validated.RecoveryFactor);
            var credential = new LocalCredential(
                identity.Id,
                validated.LoginIdentifier,
                validated.NormalizedLoginIdentifier,
                credentialVerifier);
            var completedAt = SecurityTime.GetUtcNow(timeProvider);
            var provisioningFact = new InstallationProvisioningFact(
                request.CommandId,
                identity.Id,
                completedAt,
                validated.IntentFingerprint,
                credentialVerifier,
                recoveryVerifier);

            dbContext.Identities.Add(identity);
            dbContext.LocalCredentials.Add(credential);
            dbContext.ResponsibilityAssignments.Add(new ResponsibilityAssignment(
                identity.Id,
                FunctionalResponsibility.GeneralConfiguration));
            dbContext.InstallationRecoveryStates.Add(new InstallationRecoveryState(
                recoveryVerifier,
                generation: 1,
                establishedAt: completedAt));
            dbContext.InstallationProvisioningFacts.Add(provisioningFact);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Initial provisioning succeeded. CommandId: {CommandId} IdentityId: {IdentityId}",
                request.CommandId,
                identity.Id);
            return InitialProvisioningResult.Succeeded(
                identity.Id,
                request.CommandId,
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
                "Initial provisioning rejected because the login identifier is already in use. CommandId: {CommandId}",
                request.CommandId);
            return InitialProvisioningResult.DuplicateLogin();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogError(
                exception,
                "Initial provisioning failed due to infrastructure. CommandId: {CommandId}",
                request.CommandId);
            return InitialProvisioningResult.InfrastructureFailure();
        }
    }

    private bool IsMatchingRetry(
        InstallationProvisioningFact fact,
        Guid commandId,
        ValidatedInitialProvisioning request) =>
        fact.Origin == InstallationProvisioningOrigin.InitialProvisioning &&
        fact.ProvisioningCommandId == commandId &&
        fact.RetryIntentFingerprint is not null &&
        fact.RetryIntentFingerprint.SequenceEqual(request.IntentFingerprint) &&
        fact.RetrySecretVerifier is not null &&
        secretVerifier.Verify(fact.RetrySecretVerifier, request.Secret) !=
            SecretVerificationResult.Failed &&
        fact.RetryRecoveryFactorVerifier is not null &&
        recoveryFactorVerifier.Verify(
            fact.RetryRecoveryFactorVerifier,
            request.RecoveryFactor) != SecretVerificationResult.Failed;

    private static ValidatedInitialProvisioning? Validate(
        InitialProvisioningRequest request)
    {
        if (!IsUuidVersion4(request.CommandId))
        {
            return null;
        }

        var operationalName = request.OperationalName?.Trim();
        if (string.IsNullOrWhiteSpace(operationalName))
        {
            return null;
        }

        var loginIdentifier = LoginIdentifierNormalizer.Trim(request.LoginIdentifier);
        var normalizedLoginIdentifier = LoginIdentifierNormalizer.Normalize(loginIdentifier);
        if (loginIdentifier is null || normalizedLoginIdentifier is null ||
            string.IsNullOrWhiteSpace(request.Secret) ||
            !RecoveryFactor.TryParse(request.RecoveryFactor, out var recoveryFactor))
        {
            return null;
        }

        var intentFingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[]
            {
                operationalName,
                normalizedLoginIdentifier
            })));
        return new ValidatedInitialProvisioning(
            operationalName,
            loginIdentifier,
            normalizedLoginIdentifier,
            request.Secret,
            recoveryFactor!,
            intentFingerprint);
    }

    private static bool IsUuidVersion4(Guid value) =>
        value != Guid.Empty && (value.ToByteArray(bigEndian: true)[6] >> 4) == 4;

    private sealed record ValidatedInitialProvisioning(
        string OperationalName,
        string LoginIdentifier,
        string NormalizedLoginIdentifier,
        string Secret,
        RecoveryFactor RecoveryFactor,
        byte[] IntentFingerprint);
}

public interface IInitialProvisioningService
{
    Task<InitialProvisioningResult> ProvisionAsync(
        InitialProvisioningRequest request,
        CancellationToken cancellationToken);
}

public sealed record InitialProvisioningRequest(
    Guid CommandId,
    string? OperationalName,
    string? LoginIdentifier,
    string? Secret,
    string? RecoveryFactor);

public sealed record InitialProvisioningResult(
    InitialProvisioningOutcome Outcome,
    Guid? IdentityId,
    Guid? CommandId,
    DateTimeOffset? CompletedAt)
{
    internal static InitialProvisioningResult Succeeded(
        Guid identityId,
        Guid commandId,
        DateTimeOffset completedAt) =>
        new(InitialProvisioningOutcome.Succeeded, identityId, commandId, completedAt);

    internal static InitialProvisioningResult ReplayedSuccess(
        Guid identityId,
        Guid commandId,
        DateTimeOffset completedAt) =>
        new(InitialProvisioningOutcome.ReplayedSuccess, identityId, commandId, completedAt);

    internal static InitialProvisioningResult AlreadyInitialized() =>
        new(InitialProvisioningOutcome.AlreadyInitialized, null, null, null);

    internal static InitialProvisioningResult IntentConflict() =>
        new(InitialProvisioningOutcome.IntentConflict, null, null, null);

    internal static InitialProvisioningResult InvalidInput() =>
        new(InitialProvisioningOutcome.InvalidInput, null, null, null);

    internal static InitialProvisioningResult DuplicateLogin() =>
        new(InitialProvisioningOutcome.DuplicateLogin, null, null, null);

    internal static InitialProvisioningResult InfrastructureFailure() =>
        new(InitialProvisioningOutcome.InfrastructureFailure, null, null, null);
}

public enum InitialProvisioningOutcome
{
    Succeeded,
    ReplayedSuccess,
    AlreadyInitialized,
    IntentConflict,
    InvalidInput,
    DuplicateLogin,
    InfrastructureFailure
}
