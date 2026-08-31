using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class IdentitySessionApiTests(IdentitiesAndCapabilitiesFixture fixture)
{
    [Fact]
    public async Task Antiforgery_endpoint_issues_token_and_login_requires_matching_pair()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        await fixture.ProvisionCredentialAsync(identity.Id, "ana", "secret", token);

        using var withoutToken = fixture.CreateClient();
        using var missingResponse = await withoutToken.PostAsJsonAsync(
            "/api/identity-sessions",
            new { loginIdentifier = "ana", secret = "secret" },
            token);
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
        Assert.Equal(
            "identities_and_capabilities.antiforgery_invalid",
            await ReadProblemCodeAsync(missingResponse, token));

        using var wrongToken = fixture.CreateClient();
        using (var antiforgeryResponse = await wrongToken.GetAsync(
            "/api/security/antiforgery",
            token))
        {
            antiforgeryResponse.EnsureSuccessStatusCode();
            var cookie = antiforgeryResponse.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith(
                    "nexobar-antiforgery-test=",
                    StringComparison.Ordinal));
            Assert.Contains("; path=/", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("; httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("; samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        }
        using var wrongRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/identity-sessions")
        {
            Content = JsonContent.Create(
                new { loginIdentifier = "ana", secret = "secret" })
        };
        wrongRequest.Headers.Add("X-NexoBar-CSRF", "wrong-token");
        using var wrongResponse = await wrongToken.SendAsync(wrongRequest, token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongResponse.StatusCode);

        using var valid = fixture.CreateClient();
        using var validResponse = await LoginAsync(valid, "ANA", "secret", token);
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
    }

    [Fact]
    public async Task Login_returns_minimal_identity_sets_strict_http_only_cookie_and_persists_hash()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana Operativa", true, token);
        await fixture.ProvisionCredentialAsync(
            identity.Id,
            "Ana.Login",
            " exact secret ",
            token);
        using var client = fixture.CreateClient();

        using var response = await LoginAsync(
            client,
            "  ana.login  ",
            " exact secret ",
            token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var current = await response.Content.ReadFromJsonAsync<CurrentIdentityResponse>(
            cancellationToken: token);
        Assert.Equal(
            new CurrentIdentityResponse(identity.Id, "Ana Operativa"),
            current);
        var sessionCookie = ReadSessionSetCookie(response);
        Assert.Contains("; path=/", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age=", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; secure", sessionCookie, StringComparison.OrdinalIgnoreCase);

        var rawToken = ReadSessionRawToken(sessionCookie);
        Assert.True(SessionToken.TryHash(rawToken, out var expectedHash));
        var persisted = Assert.Single(await fixture.ReadSessionsAsync(token));
        Assert.Equal(expectedHash, persisted.TokenHash);
        Assert.Equal(fixture.Clock.GetUtcNow(), persisted.CreatedAt);
        Assert.Equal(persisted.CreatedAt, persisted.LastActivityAt);
        Assert.Equal(persisted.CreatedAt.AddHours(12), persisted.AbsoluteExpiresAt);
        Assert.DoesNotContain(rawToken, await response.Content.ReadAsStringAsync(token));
    }

    [Fact]
    public void Production_cookie_defaults_enforce_host_prefix_and_secure_transport()
    {
        var options = new NexoBarSecurityCookieOptions();
        Assert.Equal("__Host-nexobar-session", options.SessionName);
        Assert.Equal("__Host-nexobar-antiforgery", options.AntiforgeryName);
        Assert.True(options.Secure);
    }

    [Fact]
    public async Task Bad_locator_bad_secret_and_inactive_identity_are_indistinguishable()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var active = await fixture.CreateIdentityAsync("Ana", true, token);
        var inactive = await fixture.CreateIdentityAsync("Beto", false, token);
        await fixture.ProvisionCredentialAsync(active.Id, "ana", "right", token);
        await fixture.ProvisionCredentialAsync(inactive.Id, "beto", "right", token);

        var failures = new[]
        {
            (Login: "missing", Secret: "right"),
            (Login: "ana", Secret: "wrong"),
            (Login: "beto", Secret: "right")
        };
        foreach (var failure in failures)
        {
            using var client = fixture.CreateClient();
            using var response = await LoginAsync(
                client,
                failure.Login,
                failure.Secret,
                token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(
                "identities_and_capabilities.invalid_credentials",
                await ReadProblemCodeAsync(response, token));
        }

        Assert.Empty(await fixture.ReadSessionsAsync(token));
    }

    [Fact]
    public async Task Successful_login_rehashes_a_legacy_verifier_without_changing_identity()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        await fixture.ProvisionCredentialAsync(identity.Id, "ana", "secret", token);
        var oldHasher = new PasswordHasher<object>(Options.Create(
            new PasswordHasherOptions
            {
                CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2
            }));
        var oldVerifier = oldHasher.HashPassword(new object(), "secret");
        await fixture.ReplaceCredentialVerifierAsync(
            identity.Id,
            oldVerifier,
            token);
        using var client = fixture.CreateClient();

        using var response = await LoginAsync(client, "ana", "secret", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var currentVerifier = await fixture.ReadCredentialVerifierAsync(identity.Id, token);
        Assert.NotEqual(oldVerifier, currentVerifier);
        Assert.Equal(
            SecretVerificationResult.Success,
            new PasswordSecretVerifier().Verify(currentVerifier, "secret"));
    }

    [Fact]
    public async Task Blank_login_input_is_a_malformed_request()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var client = fixture.CreateClient();

        using var response = await LoginAsync(client, " ", " ", token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "identities_and_capabilities.login.invalid",
            await ReadProblemCodeAsync(response, token));
    }

    [Fact]
    public async Task Current_requires_authentication_and_returns_minimal_identity()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var anonymous = fixture.CreateClient();
        using var anonymousResponse = await anonymous.GetAsync(
            "/api/identity-sessions/current",
            token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(
            "identities_and_capabilities.authentication_required",
            await ReadProblemCodeAsync(anonymousResponse, token));

        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        await fixture.ProvisionCredentialAsync(identity.Id, "ana", "secret", token);
        using var authenticated = fixture.CreateClient();
        using var login = await LoginAsync(authenticated, "ana", "secret", token);
        login.EnsureSuccessStatusCode();

        using var response = await authenticated.GetAsync(
            "/api/identity-sessions/current",
            token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(token);
        Assert.Contains(identity.Id.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ana", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("responsibil", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enablement", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logout_revokes_only_current_session_clears_cookie_and_is_context_idempotent()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        await fixture.ProvisionCredentialAsync(identity.Id, "ana", "secret", token);
        using var first = fixture.CreateClient();
        using var second = fixture.CreateClient();
        using var firstLogin = await LoginAsync(first, "ana", "secret", token);
        using var secondLogin = await LoginAsync(second, "ana", "secret", token);
        firstLogin.EnsureSuccessStatusCode();
        secondLogin.EnsureSuccessStatusCode();
        var revokedRawToken = ReadSessionRawToken(ReadSessionSetCookie(firstLogin));

        using var logout = await LogoutAsync(first, token);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var clearingCookie = logout.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(
                "nexobar-session-test=",
                StringComparison.Ordinal));
        Assert.Contains("expires=", clearingCookie, StringComparison.OrdinalIgnoreCase);
        var sessions = await fixture.ReadSessionsAsync(token);
        Assert.Equal(2, sessions.Length);
        Assert.Single(sessions, session => session.RevokedAt is not null);
        Assert.Single(sessions, session => session.RevokedAt is null);

        using var otherCurrent = await second.GetAsync(
            "/api/identity-sessions/current",
            token);
        Assert.Equal(HttpStatusCode.OK, otherCurrent.StatusCode);

        using var revokedReplay = fixture.CreateClient();
        revokedReplay.DefaultRequestHeaders.TryAddWithoutValidation(
            "Cookie",
            $"nexobar-session-test={revokedRawToken}");
        using var revokedCurrent = await revokedReplay.GetAsync(
            "/api/identity-sessions/current",
            token);
        await AssertInvalidSessionAsync(revokedCurrent, token);

        using var anonymous = fixture.CreateClient();
        using var repeated = await LogoutAsync(anonymous, token);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
    }

    [Fact]
    public async Task Logout_requires_antiforgery_even_when_context_is_idempotent()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var client = fixture.CreateClient();

        using var response = await client.DeleteAsync(
            "/api/identity-sessions/current",
            token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "identities_and_capabilities.antiforgery_invalid",
            await ReadProblemCodeAsync(response, token));
    }

    [Fact]
    public async Task Change_person_atomically_replaces_only_cookie_context_session()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var firstIdentity = await fixture.CreateIdentityAsync("Ana", true, token);
        var secondIdentity = await fixture.CreateIdentityAsync("Beto", true, token);
        await fixture.ProvisionCredentialAsync(firstIdentity.Id, "ana", "one", token);
        await fixture.ProvisionCredentialAsync(secondIdentity.Id, "beto", "two", token);
        using var changedContext = fixture.CreateClient();
        using var unrelatedContext = fixture.CreateClient();
        using var firstLogin = await LoginAsync(changedContext, "ana", "one", token);
        using var unrelatedLogin = await LoginAsync(unrelatedContext, "ana", "one", token);
        firstLogin.EnsureSuccessStatusCode();
        unrelatedLogin.EnsureSuccessStatusCode();
        var replacedToken = ReadSessionRawToken(ReadSessionSetCookie(firstLogin));
        Assert.True(SessionToken.TryHash(replacedToken, out var replacedHash));

        using var change = await LoginAsync(changedContext, "beto", "two", token);

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var sessions = await fixture.ReadSessionsAsync(token);
        Assert.Equal(3, sessions.Length);
        Assert.NotNull(Assert.Single(sessions, candidate =>
            candidate.TokenHash.SequenceEqual(replacedHash)).RevokedAt);
        Assert.Single(sessions, candidate =>
            candidate.IdentityId == firstIdentity.Id && candidate.RevokedAt is null);
        Assert.Single(sessions, candidate =>
            candidate.IdentityId == secondIdentity.Id && candidate.RevokedAt is null);
    }

    [Fact]
    public async Task Inactive_revoked_inactive_timeout_and_absolute_expiration_are_rejected()
    {
        var token = TestContext.Current.CancellationToken;

        await fixture.ResetAsync(token);
        var inactiveIdentity = await fixture.CreateIdentityAsync("Ana", true, token);
        await fixture.ProvisionCredentialAsync(
            inactiveIdentity.Id,
            "ana",
            "secret",
            token);
        using (var client = fixture.CreateClient())
        {
            using var login = await LoginAsync(client, "ana", "secret", token);
            login.EnsureSuccessStatusCode();
            await fixture.SetIdentityActiveAsync(inactiveIdentity.Id, false, token);
            using var response = await client.GetAsync(
                "/api/identity-sessions/current",
                token);
            await AssertInvalidSessionAsync(response, token);
        }

        await fixture.ResetAsync(token);
        var timeoutIdentity = await fixture.CreateIdentityAsync("Beto", true, token);
        await fixture.ProvisionCredentialAsync(
            timeoutIdentity.Id,
            "beto",
            "secret",
            token);
        using (var client = fixture.CreateClient())
        {
            using var login = await LoginAsync(client, "beto", "secret", token);
            login.EnsureSuccessStatusCode();
            fixture.Clock.Advance(TimeSpan.FromMinutes(30));
            using var response = await client.GetAsync(
                "/api/identity-sessions/current",
                token);
            await AssertInvalidSessionAsync(response, token);
        }

        await fixture.ResetAsync(token);
        var absoluteIdentity = await fixture.CreateIdentityAsync("Caro", true, token);
        await fixture.ProvisionCredentialAsync(
            absoluteIdentity.Id,
            "caro",
            "secret",
            token);
        using (var client = fixture.CreateClient())
        {
            using var login = await LoginAsync(client, "caro", "secret", token);
            login.EnsureSuccessStatusCode();
            for (var interval = 0; interval < 24; interval++)
            {
                fixture.Clock.Advance(TimeSpan.FromMinutes(29));
                using var active = await client.GetAsync(
                    "/api/identity-sessions/current",
                    token);
                Assert.Equal(HttpStatusCode.OK, active.StatusCode);
            }
            fixture.Clock.Advance(TimeSpan.FromMinutes(24));
            using var response = await client.GetAsync(
                "/api/identity-sessions/current",
                token);
            await AssertInvalidSessionAsync(response, token);
            var persisted = Assert.Single(await fixture.ReadSessionsAsync(token));
            Assert.True(persisted.LastActivityAt < persisted.AbsoluteExpiresAt);
        }
    }

    [Fact]
    public async Task Activity_refresh_is_throttled_and_never_revives_expired_session()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        await fixture.ProvisionCredentialAsync(identity.Id, "ana", "secret", token);
        using var client = fixture.CreateClient();
        using var login = await LoginAsync(client, "ana", "secret", token);
        login.EnsureSuccessStatusCode();
        var initial = Assert.Single(await fixture.ReadSessionsAsync(token));

        fixture.Clock.Advance(TimeSpan.FromSeconds(59));
        using (var belowThrottle = await client.GetAsync(
            "/api/identity-sessions/current",
            token))
        {
            Assert.Equal(HttpStatusCode.OK, belowThrottle.StatusCode);
        }
        Assert.Equal(
            initial.LastActivityAt,
            Assert.Single(await fixture.ReadSessionsAsync(token)).LastActivityAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        using (var refreshed = await client.GetAsync(
            "/api/identity-sessions/current",
            token))
        {
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        }
        var afterRefresh = Assert.Single(await fixture.ReadSessionsAsync(token));
        Assert.Equal(fixture.Clock.GetUtcNow(), afterRefresh.LastActivityAt);

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        using (var expired = await client.GetAsync(
            "/api/identity-sessions/current",
            token))
        {
            await AssertInvalidSessionAsync(expired, token);
        }
        var afterExpiration = Assert.Single(await fixture.ReadSessionsAsync(token));
        Assert.Equal(afterRefresh.LastActivityAt, afterExpiration.LastActivityAt);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        using var stillExpired = await client.GetAsync(
            "/api/identity-sessions/current",
            token);
        Assert.Equal(HttpStatusCode.Unauthorized, stillExpired.StatusCode);
        Assert.Equal(
            afterRefresh.LastActivityAt,
            Assert.Single(await fixture.ReadSessionsAsync(token)).LastActivityAt);
    }

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string loginIdentifier,
        string secret,
        CancellationToken cancellationToken)
    {
        var requestToken = await GetAntiforgeryTokenAsync(client, cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity-sessions")
        {
            Content = JsonContent.Create(new { loginIdentifier, secret })
        };
        request.Headers.Add("X-NexoBar-CSRF", requestToken);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> LogoutAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var requestToken = await GetAntiforgeryTokenAsync(client, cancellationToken);
        var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "/api/identity-sessions/current");
        request.Headers.Add("X-NexoBar-CSRF", requestToken);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            "/api/security/antiforgery",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<AntiforgeryTokenResponse>(
            cancellationToken: cancellationToken);
        return Assert.IsType<AntiforgeryTokenResponse>(payload).RequestToken;
    }

    private static string ReadSessionSetCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(
                "nexobar-session-test=",
                StringComparison.Ordinal));

    private static string ReadSessionRawToken(string setCookie)
    {
        var pair = setCookie.Split(';', 2)[0];
        return pair[(pair.IndexOf('=') + 1)..];
    }

    private static async Task<string> ReadProblemCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task AssertInvalidSessionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            "identities_and_capabilities.invalid_session",
            await ReadProblemCodeAsync(response, cancellationToken));
    }
}
