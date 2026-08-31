using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.OperationalConfiguration;
using Npgsql;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class IdentityAdministrationService(
    IdentitiesAndCapabilitiesDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    ISecretVerifier secretVerifier,
    IPreparationResponsibilityLookup preparationResponsibilities,
    TimeProvider timeProvider)
{
    private const long AdministrationLockKey = 0x494341444D494E00;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    internal Task<IdentityAdministrationResult> CreateIdentityAsync(
        Guid idempotencyKey,
        CreateIdentityRequest request,
        CancellationToken cancellationToken)
    {
        var operationalName = request.OperationalName?.Trim();
        if (string.IsNullOrWhiteSpace(operationalName))
        {
            return Task.FromResult(IdentityAdministrationResult.Invalid(
                "operationalName"));
        }

        var isActive = request.IsActive ?? false;
        var fingerprint = Fingerprint(operationalName, isActive.ToString());
        return ExecuteAsync(
            idempotencyKey,
            AdministrativeCommandKind.CreateIdentity,
            fingerprint,
            async (_, token) =>
            {
                var identity = new Identity(operationalName, isActive);
                dbContext.Identities.Add(identity);
                await dbContext.SaveChangesAsync(token);
                return IdentityAdministrationResult.Succeeded(
                    await MapAsync(identity.Id, token));
            },
            cancellationToken);
    }

    internal Task<IdentityAdministrationResult> ChangeOperationalNameAsync(
        Guid idempotencyKey,
        Guid identityId,
        ChangeIdentityOperationalNameRequest request,
        CancellationToken cancellationToken)
    {
        var operationalName = request.OperationalName?.Trim();
        if (string.IsNullOrWhiteSpace(operationalName))
        {
            return Task.FromResult(IdentityAdministrationResult.Invalid(
                "operationalName"));
        }

        return ExecuteAsync(
            idempotencyKey,
            AdministrativeCommandKind.ChangeOperationalName,
            Fingerprint(identityId.ToString("D"), operationalName),
            async (_, token) =>
            {
                var identity = await FindForUpdateAsync(identityId, token);
                if (identity is null)
                {
                    return IdentityAdministrationResult.NotFound();
                }

                identity.ChangeOperationalName(operationalName);
                await dbContext.SaveChangesAsync(token);
                return IdentityAdministrationResult.Succeeded(
                    await MapAsync(identityId, token));
            },
            cancellationToken);
    }

    internal Task<IdentityAdministrationResult> ActivateAsync(
        Guid idempotencyKey,
        Guid identityId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            idempotencyKey,
            AdministrativeCommandKind.ActivateIdentity,
            Fingerprint(identityId.ToString("D")),
            async (_, token) =>
            {
                var identity = await FindForUpdateAsync(identityId, token);
                if (identity is null)
                {
                    return IdentityAdministrationResult.NotFound();
                }

                identity.Activate();
                await dbContext.SaveChangesAsync(token);
                return IdentityAdministrationResult.Succeeded(
                    await MapAsync(identityId, token));
            },
            cancellationToken);

    internal Task<IdentityAdministrationResult> DeactivateAsync(
        Guid idempotencyKey,
        Guid identityId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            idempotencyKey,
            AdministrativeCommandKind.DeactivateIdentity,
            Fingerprint(identityId.ToString("D")),
            async (_, token) =>
            {
                var identity = await FindForUpdateAsync(identityId, token);
                if (identity is null)
                {
                    return IdentityAdministrationResult.NotFound();
                }

                if (identity.IsActive &&
                    await IsOperationalGeneralConfigurationPathAsync(identityId, token) &&
                    await CountOperationalGeneralConfigurationPathsAsync(token) <= 1)
                {
                    return IdentityAdministrationResult.LastGeneralConfigurationPath();
                }

                identity.Deactivate();
                await RevokeSessionsAsync(identityId, token);
                await dbContext.SaveChangesAsync(token);
                return IdentityAdministrationResult.Succeeded(
                    await MapAsync(identityId, token));
            },
            cancellationToken);

    internal Task<IdentityAdministrationResult> SetCredentialAsync(
        Guid idempotencyKey,
        Guid identityId,
        SetLocalCredentialRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Secret))
        {
            return Task.FromResult(IdentityAdministrationResult.Invalid("secret"));
        }

        string? requestedLoginIdentifier = null;
        if (request.LoginIdentifier is not null)
        {
            requestedLoginIdentifier = LoginIdentifierNormalizer.Trim(
                request.LoginIdentifier);
            if (requestedLoginIdentifier is null)
            {
                return Task.FromResult(IdentityAdministrationResult.Invalid(
                    "loginIdentifier"));
            }
        }

        var intentSecretVerifier = secretVerifier.Hash(request.Secret);
        return ExecuteAsync(
            idempotencyKey,
            AdministrativeCommandKind.SetLocalCredential,
            Fingerprint(
                identityId.ToString("D"),
                requestedLoginIdentifier),
            async (_, token) =>
            {
                if (await FindForUpdateAsync(identityId, token) is null)
                {
                    return IdentityAdministrationResult.NotFound();
                }

                var credential = await dbContext.LocalCredentials.SingleOrDefaultAsync(
                    candidate => candidate.IdentityId == identityId,
                    token);
                var loginIdentifier = requestedLoginIdentifier ??
                    credential?.LoginIdentifier;
                if (loginIdentifier is null)
                {
                    return IdentityAdministrationResult.Invalid("loginIdentifier");
                }

                var normalizedLoginIdentifier =
                    LoginIdentifierNormalizer.Normalize(loginIdentifier)!;
                if (credential is null)
                {
                    dbContext.LocalCredentials.Add(new LocalCredential(
                        identityId,
                        loginIdentifier,
                        normalizedLoginIdentifier,
                        intentSecretVerifier));
                }
                else
                {
                    credential.Replace(
                        loginIdentifier,
                        normalizedLoginIdentifier,
                        intentSecretVerifier);
                }

                await RevokeSessionsAsync(identityId, token);
                await dbContext.SaveChangesAsync(token);
                return IdentityAdministrationResult.Succeeded(
                    await MapAsync(identityId, token));
            },
            cancellationToken,
            request.Secret,
            intentSecretVerifier);
    }

    internal Task<IdentityAdministrationResult> AssignResponsibilityAsync(
        Guid idempotencyKey,
        Guid identityId,
        FunctionalResponsibility responsibility,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            idempotencyKey,
            AdministrativeCommandKind.AssignResponsibility,
            Fingerprint(identityId.ToString("D"), responsibility.ToString()),
            async (_, token) =>
            {
                if (await FindForUpdateAsync(identityId, token) is null)
                {
                    return IdentityAdministrationResult.NotFound();
                }

                var exists = await dbContext.ResponsibilityAssignments.AnyAsync(
                    assignment =>
                        assignment.IdentityId == identityId &&
                        assignment.ResponsibilityCode == responsibility,
                    token);
                if (!exists)
                {
                    dbContext.ResponsibilityAssignments.Add(
                        new ResponsibilityAssignment(identityId, responsibility));
                    await dbContext.SaveChangesAsync(token);
                }

                return IdentityAdministrationResult.Succeeded(
                    await MapAsync(identityId, token));
            },
            cancellationToken);

    internal Task<IdentityAdministrationResult> RevokeResponsibilityAsync(
        Guid idempotencyKey,
        Guid identityId,
        FunctionalResponsibility responsibility,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            idempotencyKey,
            AdministrativeCommandKind.RevokeResponsibility,
            Fingerprint(identityId.ToString("D"), responsibility.ToString()),
            async (_, token) =>
            {
                if (await FindForUpdateAsync(identityId, token) is null)
                {
                    return IdentityAdministrationResult.NotFound();
                }

                var exists = await dbContext.ResponsibilityAssignments.AnyAsync(
                    assignment =>
                        assignment.IdentityId == identityId &&
                        assignment.ResponsibilityCode == responsibility,
                    token);
                if (exists &&
                    responsibility == FunctionalResponsibility.GeneralConfiguration &&
                    await IsOperationalGeneralConfigurationPathAsync(identityId, token) &&
                    await CountOperationalGeneralConfigurationPathsAsync(token) <= 1)
                {
                    return IdentityAdministrationResult.LastGeneralConfigurationPath();
                }

                if (exists)
                {
                    await dbContext.ResponsibilityAssignments
                        .Where(assignment =>
                            assignment.IdentityId == identityId &&
                            assignment.ResponsibilityCode == responsibility)
                        .ExecuteDeleteAsync(token);
                }

                return IdentityAdministrationResult.Succeeded(
                    await MapAsync(identityId, token));
            },
            cancellationToken);

    internal Task<IdentityAdministrationResult> GrantPreparationEnablementAsync(
        Guid idempotencyKey,
        Guid identityId,
        Guid preparationResponsibilityId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            idempotencyKey,
            AdministrativeCommandKind.GrantPreparationEnablement,
            Fingerprint(
                identityId.ToString("D"),
                preparationResponsibilityId.ToString("D")),
            async (_, token) =>
            {
                if (await FindForUpdateAsync(identityId, token) is null)
                {
                    return IdentityAdministrationResult.NotFound();
                }

                if (!await preparationResponsibilities.ExistsAsync(
                        preparationResponsibilityId,
                        token))
                {
                    return IdentityAdministrationResult
                        .PreparationResponsibilityNotFound();
                }

                var exists = await dbContext.PreparationEnablements.AnyAsync(
                    enablement =>
                        enablement.IdentityId == identityId &&
                        enablement.PreparationResponsibilityId ==
                            preparationResponsibilityId,
                    token);
                if (!exists)
                {
                    dbContext.PreparationEnablements.Add(new PreparationEnablement(
                        identityId,
                        preparationResponsibilityId));
                    await dbContext.SaveChangesAsync(token);
                }

                return IdentityAdministrationResult.Succeeded(
                    await MapAsync(identityId, token));
            },
            cancellationToken);

    internal Task<IdentityAdministrationResult> RevokePreparationEnablementAsync(
        Guid idempotencyKey,
        Guid identityId,
        Guid preparationResponsibilityId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            idempotencyKey,
            AdministrativeCommandKind.RevokePreparationEnablement,
            Fingerprint(
                identityId.ToString("D"),
                preparationResponsibilityId.ToString("D")),
            async (_, token) =>
            {
                if (await FindForUpdateAsync(identityId, token) is null)
                {
                    return IdentityAdministrationResult.NotFound();
                }

                await dbContext.PreparationEnablements
                    .Where(enablement =>
                        enablement.IdentityId == identityId &&
                        enablement.PreparationResponsibilityId ==
                            preparationResponsibilityId)
                    .ExecuteDeleteAsync(token);
                return IdentityAdministrationResult.Succeeded(
                    await MapAsync(identityId, token));
            },
            cancellationToken);

    internal async Task<IdentityAdministrationListResult> ListAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var stabilized = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (stabilized is null)
        {
            return IdentityAdministrationListResult.AuthenticationRequired();
        }

        if (!await StabilizeGeneralConfigurationAsync(
                stabilized.IdentityId,
                cancellationToken))
        {
            return IdentityAdministrationListResult.GeneralConfigurationRequired();
        }

        var identityIds = await dbContext.Identities.AsNoTracking()
            .OrderBy(identity => identity.OperationalName)
            .ThenBy(identity => identity.Id)
            .Select(identity => identity.Id)
            .ToArrayAsync(cancellationToken);
        var identities = new List<IdentityAdministrationResponse>(identityIds.Length);
        foreach (var identityId in identityIds)
        {
            identities.Add(await MapAsync(identityId, cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return IdentityAdministrationListResult.Succeeded(identities);
    }

    private async Task<IdentityAdministrationResult> ExecuteAsync(
        Guid idempotencyKey,
        AdministrativeCommandKind commandKind,
        byte[] intentFingerprint,
        Func<Guid, CancellationToken, Task<IdentityAdministrationResult>> mutation,
        CancellationToken cancellationToken,
        string? intentSecret = null,
        string? intentSecretVerifier = null)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({AdministrationLockKey})",
            cancellationToken);

        var stabilized = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (stabilized is null)
        {
            return IdentityAdministrationResult.AuthenticationRequired();
        }

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
                    commandKind,
                    intentFingerprint) ||
                !SecretIntentMatches(existingCommand, intentSecret))
            {
                return IdentityAdministrationResult.IdempotencyConflict();
            }

            return IdentityAdministrationResult.Succeeded(
                JsonSerializer.Deserialize<IdentityAdministrationResponse>(
                    existingCommand.ResultPayload,
                    JsonOptions) ?? throw new InvalidOperationException(
                        "A durable administrative command has no readable result."));
        }

        if (!await StabilizeGeneralConfigurationAsync(
                stabilized.IdentityId,
                cancellationToken))
        {
            return IdentityAdministrationResult.GeneralConfigurationRequired();
        }

        try
        {
            var result = await mutation(stabilized.IdentityId, cancellationToken);
            if (result.Outcome != IdentityAdministrationOutcome.Succeeded)
            {
                return result;
            }

            var resultPayload = JsonSerializer.Serialize(result.Identity, JsonOptions);
            dbContext.AdministrativeCommands.Add(new IdentityAdministrativeCommand(
                idempotencyKey,
                stabilized.IdentityId,
                commandKind,
                intentFingerprint,
                intentSecretVerifier,
                resultPayload));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "UX_local_credential_normalized_login"
            })
        {
            await transaction.RollbackAsync(cancellationToken);
            return IdentityAdministrationResult.DuplicateLoginIdentifier();
        }
    }

    private async Task<bool> StabilizeGeneralConfigurationAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        var code = FunctionalResponsibility.GeneralConfiguration.ToString();
        return await dbContext.ResponsibilityAssignments
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

    private Task<Identity?> FindForUpdateAsync(
        Guid identityId,
        CancellationToken cancellationToken) =>
        dbContext.Identities
            .FromSqlInterpolated(
                $"SELECT * FROM identities_and_capabilities.identities WHERE id = {identityId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private Task<bool> IsOperationalGeneralConfigurationPathAsync(
        Guid identityId,
        CancellationToken cancellationToken) =>
        (
            from identity in dbContext.Identities
            join assignment in dbContext.ResponsibilityAssignments
                on identity.Id equals assignment.IdentityId
            join credential in dbContext.LocalCredentials
                on identity.Id equals credential.IdentityId
            where identity.Id == identityId &&
                identity.IsActive &&
                assignment.ResponsibilityCode ==
                    FunctionalResponsibility.GeneralConfiguration
            select identity.Id
        ).AnyAsync(cancellationToken);

    private Task<int> CountOperationalGeneralConfigurationPathsAsync(
        CancellationToken cancellationToken) =>
        (
            from identity in dbContext.Identities
            join assignment in dbContext.ResponsibilityAssignments
                on identity.Id equals assignment.IdentityId
            join credential in dbContext.LocalCredentials
                on identity.Id equals credential.IdentityId
            where identity.IsActive &&
                assignment.ResponsibilityCode ==
                    FunctionalResponsibility.GeneralConfiguration
            select identity.Id
        ).Distinct().CountAsync(cancellationToken);

    private Task<int> RevokeSessionsAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        var revokedAt = SecurityTime.GetUtcNow(timeProvider);
        return dbContext.Sessions
            .Where(session =>
                session.IdentityId == identityId &&
                session.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    session => session.RevokedAt,
                    revokedAt),
                cancellationToken);
    }

    private async Task<IdentityAdministrationResponse> MapAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        var identity = await dbContext.Identities.AsNoTracking()
            .Where(candidate => candidate.Id == identityId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.OperationalName,
                candidate.IsActive
            })
            .SingleAsync(cancellationToken);
        var loginIdentifier = await dbContext.LocalCredentials.AsNoTracking()
            .Where(credential => credential.IdentityId == identityId)
            .Select(credential => credential.LoginIdentifier)
            .SingleOrDefaultAsync(cancellationToken);
        var responsibilities = await dbContext.ResponsibilityAssignments.AsNoTracking()
            .Where(assignment => assignment.IdentityId == identityId)
            .Select(assignment => assignment.ResponsibilityCode)
            .ToArrayAsync(cancellationToken);
        var enablements = await dbContext.PreparationEnablements.AsNoTracking()
            .Where(enablement => enablement.IdentityId == identityId)
            .OrderBy(enablement => enablement.PreparationResponsibilityId)
            .Select(enablement => enablement.PreparationResponsibilityId)
            .ToArrayAsync(cancellationToken);

        return new IdentityAdministrationResponse(
            identity.Id,
            identity.OperationalName,
            identity.IsActive,
            loginIdentifier is not null,
            loginIdentifier,
            responsibilities.Select(value => value.ToString())
                .Order(StringComparer.Ordinal)
                .ToArray(),
            enablements);
    }

    private static byte[] Fingerprint(params string?[] values) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(values, JsonOptions)));

    private bool SecretIntentMatches(
        IdentityAdministrativeCommand command,
        string? intentSecret)
    {
        if (command.IntentSecretVerifier is null)
        {
            return intentSecret is null;
        }

        return intentSecret is not null &&
            secretVerifier.Verify(command.IntentSecretVerifier, intentSecret) !=
                SecretVerificationResult.Failed;
    }
}
