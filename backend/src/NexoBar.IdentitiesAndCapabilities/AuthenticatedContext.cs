using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace NexoBar.IdentitiesAndCapabilities;

public interface IAuthenticatedContext
{
    bool IsAuthenticated { get; }

    Guid? IdentityId { get; }

    Guid? SessionId { get; }
}

internal sealed class HttpAuthenticatedContext(IHttpContextAccessor httpContextAccessor) :
    IAuthenticatedContext
{
    public bool IsAuthenticated =>
        IdentityId is not null && SessionId is not null;

    public Guid? IdentityId => ReadGuidClaim(ClaimTypes.NameIdentifier);

    public Guid? SessionId => ReadGuidClaim(SessionAuthenticationDefaults.SessionIdClaim);

    private Guid? ReadGuidClaim(string claimType)
    {
        var value = httpContextAccessor.HttpContext?.User.FindFirstValue(claimType);
        return Guid.TryParse(value, out var identifier) ? identifier : null;
    }
}
