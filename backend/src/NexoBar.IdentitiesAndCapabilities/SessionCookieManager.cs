using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class SessionCookieManager(
    IOptions<NexoBarSecurityCookieOptions> options)
{
    private readonly NexoBarSecurityCookieOptions options = options.Value;

    internal string Name => options.SessionName;

    internal void Issue(HttpResponse response, string rawToken) =>
        response.Cookies.Append(options.SessionName, rawToken, CreateOptions());

    internal void Clear(HttpResponse response) =>
        response.Cookies.Delete(options.SessionName, CreateOptions());

    internal CookieOptions CreateOptions() => new()
    {
        HttpOnly = true,
        Secure = options.Secure,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true
    };
}
