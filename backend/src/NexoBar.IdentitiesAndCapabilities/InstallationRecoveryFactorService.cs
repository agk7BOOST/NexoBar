using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class InstallationRecoveryFactorService(
    IdentitiesAndCapabilitiesDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IRecoveryFactorVerifier recoveryFactorVerifier,
    TimeProvider timeProvider)
{
    internal const long RecoveryLockKey = 0x49435245434F5600;
    private static readonly byte[] IntentFingerprint = [1];
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    internal async Task<InstallationRecoveryFactorResult> RotateAsync(
        Guid idempotencyKey,
        InstallationRecoveryFactorRequest request,
        CancellationToken cancellationToken)
    {
        if (!RecoveryFactor.TryParse(request.NewRecoveryFactor, out var factor))
        {
            return InstallationRecoveryFactorResult.InvalidRecoveryFactor();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var stabilized = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (stabilized is null)
        {
            return InstallationRecoveryFactorResult.AuthenticationRequired();
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({RecoveryLockKey})",
            cancellationToken);

        var existingCommand = await dbContext.AdministrativeCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            if (!existingCommand.Matches(
                    stabilized.IdentityId,
                    AdministrativeCommandKind.RotateInstallationRecoveryFactor,
                    IntentFingerprint) ||
                existingCommand.IntentSecretVerifier is null ||
                recoveryFactorVerifier.Verify(
                    existingCommand.IntentSecretVerifier,
                    factor!) == SecretVerificationResult.Failed)
            {
                return InstallationRecoveryFactorResult.IdempotencyConflict();
            }

            return InstallationRecoveryFactorResult.Succeeded(
                JsonSerializer.Deserialize<InstallationRecoveryFactorResponse>(
                    existingCommand.ResultPayload,
                    JsonOptions) ?? throw new InvalidOperationException(
                        "A durable recovery factor command has no readable result."));
        }

        if (!await HasGeneralConfigurationAsync(
                stabilized.IdentityId,
                cancellationToken))
        {
            return InstallationRecoveryFactorResult.GeneralConfigurationRequired();
        }

        var state = await dbContext.InstallationRecoveryStates.SingleOrDefaultAsync(
            cancellationToken);
        if (state is null && !await dbContext.InstallationProvisioningFacts.AnyAsync(
                cancellationToken))
        {
            return InstallationRecoveryFactorResult.InconsistentInstallationState();
        }

        if (state is not null && state.Generation == int.MaxValue)
        {
            return InstallationRecoveryFactorResult.GenerationExhausted();
        }

        var stateVerifier = recoveryFactorVerifier.Hash(factor!);
        var retryVerifier = recoveryFactorVerifier.Hash(factor!);
        var now = SecurityTime.GetUtcNow(timeProvider);
        InstallationRecoveryFactorResponse response;
        if (state is null)
        {
            state = new InstallationRecoveryState(stateVerifier, 1, now);
            dbContext.InstallationRecoveryStates.Add(state);
            response = new InstallationRecoveryFactorResponse(1, now, null);
        }
        else
        {
            var generation = checked(state.Generation + 1);
            state.Rotate(stateVerifier, generation, now);
            response = new InstallationRecoveryFactorResponse(
                generation,
                state.EstablishedAt,
                now);
        }

        dbContext.AdministrativeCommands.Add(new IdentityAdministrativeCommand(
            idempotencyKey,
            stabilized.IdentityId,
            AdministrativeCommandKind.RotateInstallationRecoveryFactor,
            IntentFingerprint,
            retryVerifier,
            JsonSerializer.Serialize(response, JsonOptions)));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InstallationRecoveryFactorResult.Succeeded(response);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "PK_administrative_commands"
            })
        {
            await transaction.RollbackAsync(cancellationToken);
            return InstallationRecoveryFactorResult.IdempotencyConflict();
        }
    }

    private Task<bool> HasGeneralConfigurationAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        var code = FunctionalResponsibility.GeneralConfiguration.ToString();
        return dbContext.ResponsibilityAssignments
            .FromSqlInterpolated(
                $"""
                SELECT identity_id, responsibility_code
                FROM identities_and_capabilities.responsibility_assignments
                WHERE identity_id = {identityId}
                  AND responsibility_code = {code}
                FOR SHARE
                """)
            .AsNoTracking()
            .AnyAsync(cancellationToken);
    }
}

internal sealed record InstallationRecoveryFactorRequest(string? NewRecoveryFactor);

internal sealed record InstallationRecoveryFactorResponse(
    int Generation,
    DateTimeOffset EstablishedAt,
    DateTimeOffset? LastRotatedAt);

internal sealed record InstallationRecoveryFactorResult(
    InstallationRecoveryFactorOutcome Outcome,
    InstallationRecoveryFactorResponse? Response)
{
    internal static InstallationRecoveryFactorResult Succeeded(
        InstallationRecoveryFactorResponse response) =>
        new(InstallationRecoveryFactorOutcome.Succeeded, response);

    internal static InstallationRecoveryFactorResult InvalidRecoveryFactor() =>
        new(InstallationRecoveryFactorOutcome.InvalidRecoveryFactor, null);

    internal static InstallationRecoveryFactorResult AuthenticationRequired() =>
        new(InstallationRecoveryFactorOutcome.AuthenticationRequired, null);

    internal static InstallationRecoveryFactorResult GeneralConfigurationRequired() =>
        new(InstallationRecoveryFactorOutcome.GeneralConfigurationRequired, null);

    internal static InstallationRecoveryFactorResult IdempotencyConflict() =>
        new(InstallationRecoveryFactorOutcome.IdempotencyConflict, null);

    internal static InstallationRecoveryFactorResult InconsistentInstallationState() =>
        new(InstallationRecoveryFactorOutcome.InconsistentInstallationState, null);

    internal static InstallationRecoveryFactorResult GenerationExhausted() =>
        new(InstallationRecoveryFactorOutcome.GenerationExhausted, null);
}

internal enum InstallationRecoveryFactorOutcome
{
    Succeeded,
    InvalidRecoveryFactor,
    AuthenticationRequired,
    GeneralConfigurationRequired,
    IdempotencyConflict,
    InconsistentInstallationState,
    GenerationExhausted
}
