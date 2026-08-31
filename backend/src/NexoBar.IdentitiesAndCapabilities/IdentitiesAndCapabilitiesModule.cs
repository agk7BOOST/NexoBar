using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NexoBar.IdentitiesAndCapabilities;

public static class IdentitiesAndCapabilitiesModule
{
    public static IServiceCollection AddIdentitiesAndCapabilities(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(
            "IdentitiesAndCapabilities")
            ?? throw new InvalidOperationException(
                "Connection string 'IdentitiesAndCapabilities' is required.");

        services.AddDbContext<IdentitiesAndCapabilitiesDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "identities_and_capabilities")));
        services.AddOptions<ProvisionalSessionPolicyOptions>()
            .Bind(configuration.GetSection(ProvisionalSessionPolicyOptions.SectionName))
            .Validate(
                policy =>
                    policy.InactivityTimeout > TimeSpan.Zero &&
                    policy.AbsoluteLifetime > policy.InactivityTimeout &&
                    policy.ActivityRefreshInterval > TimeSpan.Zero &&
                    policy.ActivityRefreshInterval < policy.InactivityTimeout,
                "Provisional session policy intervals are invalid.")
            .ValidateOnStart();
        services.AddOptions<NexoBarSecurityCookieOptions>()
            .Bind(configuration.GetSection(NexoBarSecurityCookieOptions.SectionName))
            .Validate(
                cookies =>
                    IsValidCookieName(cookies.SessionName) &&
                    IsValidCookieName(cookies.AntiforgeryName) &&
                    (!cookies.SessionName.StartsWith("__Host-", StringComparison.Ordinal) ||
                        cookies.Secure) &&
                    (!cookies.AntiforgeryName.StartsWith("__Host-", StringComparison.Ordinal) ||
                        cookies.Secure),
                "Security cookie names or Secure settings are invalid.")
            .ValidateOnStart();

        var cookieOptions = configuration
            .GetSection(NexoBarSecurityCookieOptions.SectionName)
            .Get<NexoBarSecurityCookieOptions>() ?? new NexoBarSecurityCookieOptions();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-NexoBar-CSRF";
            options.Cookie.Name = cookieOptions.AntiforgeryName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.Cookie.IsEssential = true;
            options.Cookie.SecurePolicy = cookieOptions.Secure
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.None;
        });
        services.AddAuthentication(SessionAuthenticationDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
                SessionAuthenticationDefaults.Scheme,
                _ => { });
        services.AddAuthorization();
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretVerifier, PasswordSecretVerifier>();
        services.AddScoped<SessionCookieManager>();
        services.AddScoped<IAuthenticatedContext, HttpAuthenticatedContext>();
        services.AddScoped<IAuthenticatedSessionStabilizer, AuthenticatedSessionStabilizer>();
        services.AddScoped<IPreparationCapabilityStabilizer,
            PreparationCapabilityStabilizer>();
        services.AddScoped<IPreparationAuthorization, PreparationAuthorization>();
        services.AddScoped<IOrderOperationsAuthorization,
            OrderOperationsAuthorization>();
        services.AddScoped<IdentitySessionService>();
        services.AddScoped<CurrentPreparationDestinationQueryService>();
        services.AddScoped<LocalCredentialProvisioner>();
        services.AddScoped<PreparationEnablementService>();
        services.AddScoped<IdentityAdministrationService>();

        return services;
    }

    public static IEndpointRouteBuilder MapIdentitySessionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/security/antiforgery",
                GetAntiforgeryToken)
            .AllowAnonymous()
            .WithTags("Security")
            .WithName("GetAntiforgeryToken")
            .Produces<AntiforgeryTokenResponse>();

        endpoints.MapPost(
                "/api/identity-sessions",
                LoginAsync)
            .AllowAnonymous()
            .WithTags("Identity Sessions")
            .WithName("CreateIdentitySession")
            .Accepts<LoginRequest>("application/json")
            .Produces<CurrentIdentityResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        endpoints.MapGet(
                "/api/identity-sessions/current",
                GetCurrentAsync)
            .RequireAuthorization()
            .WithTags("Identity Sessions")
            .WithName("GetCurrentIdentitySession")
            .Produces<CurrentIdentityResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        endpoints.MapDelete(
                "/api/identity-sessions/current",
                LogoutAsync)
            .AllowAnonymous()
            .WithTags("Identity Sessions")
            .WithName("DeleteCurrentIdentitySession")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        endpoints.MapGet(
                "/api/identity-sessions/current/preparation-destinations",
                GetCurrentPreparationDestinationsAsync)
            .RequireAuthorization()
            .WithTags("Identity Sessions")
            .WithName("GetCurrentPreparationDestinations")
            .Produces<IReadOnlyList<CurrentPreparationDestinationResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static async Task<IResult> GetCurrentPreparationDestinationsAsync(
        CurrentPreparationDestinationQueryService destinations,
        CancellationToken cancellationToken)
    {
        var result = await destinations.ListAsync(cancellationToken);
        return result.Outcome switch
        {
            CurrentPreparationDestinationsOutcome.Succeeded =>
                Results.Ok(result.Destinations),
            CurrentPreparationDestinationsOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            CurrentPreparationDestinationsOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Preparation access forbidden",
                "The current Identity is not authorized for Preparation.",
                "identities_and_capabilities.preparation.forbidden"),
            CurrentPreparationDestinationsOutcome.ReferenceInconsistent => Problem(
                StatusCodes.Status500InternalServerError,
                "Preparation destination reference is inconsistent",
                "An enabled Preparation destination could not be resolved.",
                "identities_and_capabilities.preparation_destination.reference_inconsistent"),
            _ => throw new InvalidOperationException(
                "Unknown Preparation destinations outcome.")
        };
    }

    private static IResult GetAntiforgeryToken(
        HttpContext httpContext,
        IAntiforgery antiforgery)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        return Results.Ok(new AntiforgeryTokenResponse(
            tokens.RequestToken ?? throw new InvalidOperationException(
                "The antiforgery stack did not issue a request token.")));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentitySessionService sessions,
        SessionCookieManager sessionCookie,
        IOptions<NexoBarSecurityCookieOptions> cookieOptions,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await ValidateAntiforgeryAsync(
            httpContext,
            antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var result = await sessions.LoginAsync(
            request.LoginIdentifier ?? string.Empty,
            request.Secret ?? string.Empty,
            cancellationToken);

        if (result.Outcome == LoginOutcome.InvalidRequest)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid login intention",
                "Login identifier and secret must contain non-whitespace text.",
                "identities_and_capabilities.login.invalid");
        }

        if (result.Outcome == LoginOutcome.InvalidCredentials)
        {
            return Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid credentials",
                "The supplied credentials are invalid or cannot be used.",
                "identities_and_capabilities.invalid_credentials");
        }

        sessionCookie.Issue(httpContext.Response, result.RawToken!);
        ClearAntiforgeryCookie(httpContext.Response, cookieOptions.Value);
        return Results.Ok(result.Identity);
    }

    private static async Task<IResult> GetCurrentAsync(
        IdentitySessionService sessions,
        SessionCookieManager sessionCookie,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var identity = await sessions.ReadCurrentAsync(cancellationToken);
        if (identity is not null)
        {
            return Results.Ok(identity);
        }

        sessionCookie.Clear(httpContext.Response);
        return Problem(
            StatusCodes.Status401Unauthorized,
            "Invalid session",
            "The current session is invalid or expired.",
            "identities_and_capabilities.invalid_session");
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentitySessionService sessions,
        SessionCookieManager sessionCookie,
        IOptions<NexoBarSecurityCookieOptions> cookieOptions,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await ValidateAntiforgeryAsync(
            httpContext,
            antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        await sessions.LogoutAsync(cancellationToken);
        sessionCookie.Clear(httpContext.Response);
        ClearAntiforgeryCookie(httpContext.Response, cookieOptions.Value);
        return Results.NoContent();
    }

    private static async Task<IResult?> ValidateAntiforgeryAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Antiforgery validation failed",
                "A valid antiforgery cookie and request token are required.",
                "identities_and_capabilities.antiforgery_invalid");
        }
    }

    private static void ClearAntiforgeryCookie(
        HttpResponse response,
        NexoBarSecurityCookieOptions options) =>
        response.Cookies.Delete(
            options.AntiforgeryName,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = options.Secure,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true
            });

    private static IResult Problem(
        int statusCode,
        string title,
        string detail,
        string code) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code
            });

    private static bool IsValidCookieName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        !name.Contains(';', StringComparison.Ordinal) &&
        !name.Contains('=', StringComparison.Ordinal);
}
