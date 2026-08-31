using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class IdentitySessionService(
    IdentitiesAndCapabilitiesDbContext dbContext,
    ISecretVerifier secretVerifier,
    IAuthenticatedContext authenticatedContext,
    TimeProvider timeProvider,
    IOptions<ProvisionalSessionPolicyOptions> policyOptions)
{
    private readonly ProvisionalSessionPolicyOptions policy = policyOptions.Value;

    internal async Task<LoginResult> LoginAsync(
        string loginIdentifier,
        string secret,
        CancellationToken cancellationToken)
    {
        var normalizedLoginIdentifier = LoginIdentifierNormalizer.Normalize(loginIdentifier);
        if (normalizedLoginIdentifier is null || string.IsNullOrWhiteSpace(secret))
        {
            return LoginResult.InvalidRequest();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);

        var credential = await dbContext.LocalCredentials.SingleOrDefaultAsync(
            candidate =>
                candidate.NormalizedLoginIdentifier == normalizedLoginIdentifier,
            cancellationToken);

        if (credential is null)
        {
            secretVerifier.VerifyDummy(secret);
            return LoginResult.InvalidCredentials();
        }

        var verification = secretVerifier.Verify(credential.SecretVerifier, secret);
        if (verification == SecretVerificationResult.Failed)
        {
            return LoginResult.InvalidCredentials();
        }

        var identity = await dbContext.Identities
            .FromSqlInterpolated(
                $"SELECT * FROM identities_and_capabilities.identities WHERE id = {credential.IdentityId} FOR SHARE")
            .SingleAsync(cancellationToken);
        if (!identity.IsActive)
        {
            return LoginResult.InvalidCredentials();
        }

        var now = SecurityTime.GetUtcNow(timeProvider);
        if (authenticatedContext.SessionId is { } replacedSessionId)
        {
            await dbContext.Sessions
                .Where(session =>
                    session.Id == replacedSessionId &&
                    session.RevokedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        session => session.RevokedAt,
                        now),
                    cancellationToken);
        }

        if (verification == SecretVerificationResult.SuccessRehashNeeded)
        {
            credential.ReplaceSecretVerifier(secretVerifier.Hash(secret));
        }

        var generatedToken = SessionToken.Generate();
        var session = new IdentitySession(
            identity.Id,
            generatedToken.TokenHash,
            now,
            now + policy.AbsoluteLifetime);
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LoginResult.Succeeded(
            generatedToken.RawToken,
            new CurrentIdentityResponse(identity.Id, identity.OperationalName));
    }

    internal async Task<CurrentIdentityResponse?> ReadCurrentAsync(
        CancellationToken cancellationToken)
    {
        if (authenticatedContext.IdentityId is not { } identityId)
        {
            return null;
        }

        return await dbContext.Identities.AsNoTracking()
            .Where(identity => identity.Id == identityId && identity.IsActive)
            .Select(identity => new CurrentIdentityResponse(
                identity.Id,
                identity.OperationalName))
            .SingleOrDefaultAsync(cancellationToken);
    }

    internal async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (authenticatedContext.SessionId is not { } sessionId)
        {
            return;
        }

        var now = SecurityTime.GetUtcNow(timeProvider);
        await dbContext.Sessions
            .Where(session => session.Id == sessionId && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAt, now),
                cancellationToken);
    }
}

internal sealed record LoginResult(
    LoginOutcome Outcome,
    string? RawToken,
    CurrentIdentityResponse? Identity)
{
    internal static LoginResult Succeeded(
        string rawToken,
        CurrentIdentityResponse identity) =>
        new(LoginOutcome.Succeeded, rawToken, identity);

    internal static LoginResult InvalidRequest() =>
        new(LoginOutcome.InvalidRequest, null, null);

    internal static LoginResult InvalidCredentials() =>
        new(LoginOutcome.InvalidCredentials, null, null);
}

internal enum LoginOutcome
{
    Succeeded,
    InvalidRequest,
    InvalidCredentials
}

internal sealed record CurrentIdentityResponse(Guid IdentityId, string OperationalName);

internal sealed record LoginRequest(string? LoginIdentifier, string? Secret);

internal sealed record AntiforgeryTokenResponse(string RequestToken);
