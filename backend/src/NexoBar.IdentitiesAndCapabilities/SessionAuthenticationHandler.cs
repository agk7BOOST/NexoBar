using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NexoBar.IdentitiesAndCapabilities;

internal static class SessionAuthenticationDefaults
{
    internal const string Scheme = "NexoBarSession";
    internal const string SessionIdClaim = "nexobar:session_id";
}

internal sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IdentitiesAndCapabilitiesDbContext dbContext,
    SessionCookieManager cookieManager,
    TimeProvider timeProvider,
    IOptions<ProvisionalSessionPolicyOptions> policyOptions) :
    AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private readonly ProvisionalSessionPolicyOptions policy = policyOptions.Value;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(cookieManager.Name, out var rawToken))
        {
            return AuthenticateResult.NoResult();
        }

        if (!SessionToken.TryHash(rawToken, out var tokenHash))
        {
            cookieManager.Clear(Response);
            return AuthenticateResult.Fail("Invalid session token.");
        }

        var session = await (
            from candidate in dbContext.Sessions.AsNoTracking()
            join identityRow in dbContext.Identities.AsNoTracking()
                on candidate.IdentityId equals identityRow.Id
            where candidate.TokenHash == tokenHash
            select new
            {
                candidate.Id,
                candidate.IdentityId,
                candidate.LastActivityAt,
                candidate.AbsoluteExpiresAt,
                candidate.RevokedAt,
                identityRow.IsActive
            }).SingleOrDefaultAsync(Context.RequestAborted);

        var now = SecurityTime.GetUtcNow(timeProvider);
        if (session is null ||
            session.RevokedAt is not null ||
            !session.IsActive ||
            now >= session.AbsoluteExpiresAt ||
            now >= session.LastActivityAt + policy.InactivityTimeout)
        {
            cookieManager.Clear(Response);
            return AuthenticateResult.Fail("Invalid or expired session.");
        }

        if (now >= session.LastActivityAt + policy.ActivityRefreshInterval)
        {
            var inactivityFloor = now - policy.InactivityTimeout;
            var refreshCeiling = now - policy.ActivityRefreshInterval;
            await dbContext.Sessions
                .Where(candidate =>
                    candidate.Id == session.Id &&
                    candidate.RevokedAt == null &&
                    candidate.AbsoluteExpiresAt > now &&
                    candidate.LastActivityAt > inactivityFloor &&
                    candidate.LastActivityAt <= refreshCeiling)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        candidate => candidate.LastActivityAt,
                        now),
                    Context.RequestAborted);
        }

        var claimsIdentity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, session.IdentityId.ToString("D")),
                new Claim(SessionAuthenticationDefaults.SessionIdClaim, session.Id.ToString("D"))
            ],
            SessionAuthenticationDefaults.Scheme);
        var principal = new ClaimsPrincipal(claimsIdentity);
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, SessionAuthenticationDefaults.Scheme));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var hasCookie = Request.Cookies.ContainsKey(cookieManager.Name);
        if (hasCookie)
        {
            cookieManager.Clear(Response);
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: hasCookie ? "Invalid session" : "Authentication required",
            detail: hasCookie
                ? "The current session is invalid or expired."
                : "A valid Identity Session is required.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = hasCookie
                    ? "identities_and_capabilities.invalid_session"
                    : "identities_and_capabilities.authentication_required"
            }).ExecuteAsync(Context);
    }
}

internal static class SecurityTime
{
    internal static DateTimeOffset GetUtcNow(TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        return new DateTimeOffset(
            now.Ticks - (now.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);
    }
}
