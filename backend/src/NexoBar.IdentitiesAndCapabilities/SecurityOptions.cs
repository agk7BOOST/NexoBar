namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class ProvisionalSessionPolicyOptions
{
    internal const string SectionName = "NexoBarSecurity:ProvisionalSessionPolicy";

    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan AbsoluteLifetime { get; init; } = TimeSpan.FromHours(12);

    public TimeSpan ActivityRefreshInterval { get; init; } = TimeSpan.FromMinutes(1);
}

internal sealed class NexoBarSecurityCookieOptions
{
    internal const string SectionName = "NexoBarSecurity:Cookies";

    public string SessionName { get; init; } = "__Host-nexobar-session";

    public string AntiforgeryName { get; init; } = "__Host-nexobar-antiforgery";

    public bool Secure { get; init; } = true;
}
