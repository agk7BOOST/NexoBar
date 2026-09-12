using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexoBar.Host.Notifications;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class SseTransportApiTests(IdentitiesAndCapabilitiesFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private ChangeNotificationHub Hub => fixture.Services.GetRequiredService<ChangeNotificationHub>();

    [Fact]
    public async Task Authorized_cookie_stream_opens_without_antiforgery_and_emits_only_scoped_invalidation()
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        await using var stream = await OpenAsync(client, Scope(actor.Destination));
        Assert.Equal(HttpStatusCode.OK, stream.Response.StatusCode);
        Assert.Equal("text/event-stream", stream.Response.Content.Headers.ContentType?.MediaType);
        Assert.True(stream.Response.Headers.CacheControl?.NoStore);
        Assert.True(stream.Response.Headers.CacheControl?.NoCache);
        Assert.Contains("no", stream.Response.Headers.GetValues("X-Accel-Buffering"));
        Assert.Equal(": connected\n", await stream.FrameAsync(Token));

        Publish(Guid.NewGuid());
        Publish(actor.Destination);
        var frame = await stream.InvalidationAsync(Token);
        Assert.Equal($"event: invalidation\ndata: {{\"kind\":\"preparation.destination.changed\",\"scopeId\":\"{actor.Destination:D}\"}}\n", frame);
        Assert.Same(Hub, fixture.Services.GetRequiredService<IChangeNotificationPublisher>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("scope=")]
    [InlineData("scope=preparation.destination:not-a-uuid")]
    [InlineData("scope=preparation.destination:00000000-0000-0000-0000-000000000000")]
    [InlineData("scope=order.changed:11111111-1111-1111-1111-111111111111")]
    [InlineData("scope=catalog.product:11111111-1111-1111-1111-111111111111")]
    public async Task Malformed_or_unsupported_snapshot_is_rejected_before_streaming(string query)
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        using var response = await client.GetAsync($"/api/notifications/stream?{query}", Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("sse.invalid_subscription", await ProblemCodeAsync(response));
        Assert.Equal(0, Hub.SubscriptionCount);
    }

    [Fact]
    public async Task Scope_count_is_bounded_and_duplicates_are_deduplicated()
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        var tooMany = string.Join('&', Enumerable.Repeat(Scope(actor.Destination), SseTransport.MaximumScopes + 1));
        using var rejected = await client.GetAsync($"/api/notifications/stream?{tooMany}", Token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        await using var stream = await OpenAsync(client, $"{Scope(actor.Destination)}&{Scope(actor.Destination)}");
        Assert.Equal(": connected\n", await stream.FrameAsync(Token));
        Publish(actor.Destination);
        await stream.InvalidationAsync(Token);
        Assert.Equal(": keep-alive\n", await stream.FrameAsync(Token));
        Assert.Equal(1, Hub.SubscriptionCount);
    }

    [Theory]
    [InlineData("no-session", HttpStatusCode.Unauthorized)]
    [InlineData("inactive", HttpStatusCode.Unauthorized)]
    [InlineData("revoked", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("no-responsibility", HttpStatusCode.Forbidden)]
    [InlineData("no-enablement", HttpStatusCode.Forbidden)]
    [InlineData("wrong-enablement", HttpStatusCode.Forbidden)]
    public async Task Initial_authority_requires_current_session_identity_responsibility_and_exact_enablement(
        string missing, HttpStatusCode expected)
    {
        var actor = await ArrangeAsync(missing);
        using var client = actor.Client;
        using var response = await client.GetAsync($"/api/notifications/stream?{Scope(actor.Destination)}", Token);
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(actor.Destination.ToString("D"), await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(0, Hub.SubscriptionCount);
    }

    [Fact]
    public async Task Unknown_and_existing_unauthorized_destinations_have_same_error_and_mixed_snapshot_fails_closed()
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        var existing = await fixture.CreatePreparationResponsibilityAsync("Other", Token);
        string? expected = null;
        foreach (var destination in new[] { existing, Guid.NewGuid() })
        {
            using var response = await client.GetAsync(
                $"/api/notifications/stream?{Scope(actor.Destination)}&{Scope(destination)}", Token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
            var visible = $"{body.GetProperty("title").GetString()}:{body.GetProperty("code").GetString()}";
            expected ??= visible;
            Assert.Equal(expected, visible);
            Assert.DoesNotContain(destination.ToString("D"), body.ToString());
            Assert.Equal(0, Hub.SubscriptionCount);
        }
    }

    [Fact]
    public async Task Duplicate_publications_are_delivered_as_independent_harmless_invalidations()
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        await using var stream = await OpenAsync(client, Scope(actor.Destination));
        Publish(actor.Destination);
        Publish(actor.Destination);
        Assert.Equal(await stream.InvalidationAsync(Token), await stream.InvalidationAsync(Token));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("inactive")]
    [InlineData("no-responsibility")]
    [InlineData("no-enablement")]
    public async Task Authority_loss_blocks_next_notification_and_closes_without_waiting_for_heartbeat(string loss)
    {
        var actor = await ArrangeAsync();
        fixture.Services.GetRequiredService<IOptions<SseTransportOptions>>().Value.HeartbeatInterval = TimeSpan.FromMinutes(1);
        using var client = actor.Client;
        await using var stream = await OpenAsync(client, Scope(actor.Destination));
        Assert.Equal(": connected\n", await stream.FrameAsync(Token));
        await LoseAuthorityAsync(actor, loss);
        Publish(actor.Destination);
        Assert.Null(await stream.FrameAsync(Token));
        await WaitForCleanupAsync();
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("no-enablement")]
    [InlineData("inactive")]
    [InlineData("no-responsibility")]
    public async Task Heartbeat_terminates_idle_stream_after_authority_loss(string loss)
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        await using var stream = await OpenAsync(client, Scope(actor.Destination));
        Assert.Equal(": connected\n", await stream.FrameAsync(Token));
        await LoseAuthorityAsync(actor, loss);
        await stream.AssertEndedWithoutInvalidationAsync(Token);
        await WaitForCleanupAsync();
    }

    [Fact]
    public async Task Opening_reopening_heartbeat_and_delivery_never_renew_session_inactivity()
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        var original = Assert.Single(await fixture.ReadSessionsAsync(Token));
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        for (var reconnect = 0; reconnect < 2; reconnect++)
        {
            await using (var stream = await OpenAsync(client, Scope(actor.Destination)))
            {
                Assert.Equal(": connected\n", await stream.FrameAsync(Token));
                fixture.Clock.Advance(TimeSpan.FromMinutes(2));
                Assert.Equal(": keep-alive\n", await stream.FrameAsync(Token));
                Publish(actor.Destination);
                await stream.InvalidationAsync(Token);
                var current = Assert.Single(await fixture.ReadSessionsAsync(Token));
                Assert.Equal(original.LastActivityAt, current.LastActivityAt);
                Assert.Equal(original.AbsoluteExpiresAt, current.AbsoluteExpiresAt);
            }
            await WaitForCleanupAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connected_session_expires_at_inactivity_or_absolute_limit(bool absolute)
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        if (absolute)
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
                .Sessions.Where(session => session.Id == actor.SessionId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.AbsoluteExpiresAt,
                    fixture.Clock.GetUtcNow() + TimeSpan.FromMinutes(5)), Token);
        }
        await using var stream = await OpenAsync(client, Scope(actor.Destination));
        Assert.Equal(": connected\n", await stream.FrameAsync(Token));
        fixture.Clock.Advance(TimeSpan.FromMinutes(absolute ? 5 : 30));
        await stream.AssertEndedWithoutInvalidationAsync(Token);
        await WaitForCleanupAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_or_acting_person_replacement_ends_old_stream(bool replacePerson)
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        await using var stream = await OpenAsync(client, Scope(actor.Destination));
        Assert.Equal(": connected\n", await stream.FrameAsync(Token));
        var csrf = await client.GetFromJsonAsync<AntiforgeryTokenResponse>("/api/security/antiforgery", Token);
        using var request = new HttpRequestMessage(
            replacePerson ? HttpMethod.Post : HttpMethod.Delete,
            replacePerson ? "/api/identity-sessions" : "/api/identity-sessions/current");
        if (replacePerson)
        {
            var next = await fixture.CreateIdentityAsync("Next actor", true, Token);
            await fixture.ProvisionCredentialAsync(next.Id, "next", "secret", Token);
            request.Content = JsonContent.Create(new { loginIdentifier = "next", secret = "secret" });
        }
        request.Headers.Add("X-NexoBar-CSRF", csrf!.RequestToken);
        using var response = await client.SendAsync(request, Token);
        response.EnsureSuccessStatusCode();
        await stream.AssertEndedWithoutInvalidationAsync(Token);
        await WaitForCleanupAsync();
    }

    [Fact]
    public async Task Browser_disconnect_and_request_cancellation_remove_subscription()
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        var stream = await OpenAsync(client, Scope(actor.Destination));
        Assert.Equal(1, Hub.SubscriptionCount);
        stream.Cancel();
        await stream.DisposeAsync();
        await WaitForCleanupAsync();
        await using (var second = await OpenAsync(client, Scope(actor.Destination)))
        {
            Assert.Equal(1, Hub.SubscriptionCount);
        }
        await WaitForCleanupAsync();
    }

    private async Task<Actor> ArrangeAsync(string? missing = null)
    {
        await WaitForCleanupAsync();
        await fixture.ResetAsync(Token);
        fixture.Services.GetRequiredService<IOptions<SseTransportOptions>>().Value.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
        var destination = await fixture.CreatePreparationResponsibilityAsync("Kitchen", Token);
        var identity = await fixture.CreateIdentityAsync("SSE actor", missing != "inactive", Token);
        if (missing != "no-responsibility")
        {
            await fixture.InsertAssignmentAsync(identity.Id, FunctionalResponsibility.Preparation, Token);
        }
        if (missing != "no-enablement")
        {
            await fixture.InsertEnablementAsync(identity.Id,
                missing == "wrong-enablement" ? Guid.NewGuid() : destination, Token);
        }
        var generated = SessionToken.Generate();
        var now = fixture.Clock.GetUtcNow();
        var session = new IdentitySession(identity.Id, generated.TokenHash, now, now + TimeSpan.FromHours(12));
        await fixture.InsertSessionAsync(session, Token);
        var client = fixture.CreateClient();
        if (missing != "no-session")
        {
            client.DefaultRequestHeaders.Add("Cookie", $"nexobar-session-test={generated.RawToken}");
        }
        var actor = new Actor(identity.Id, session.Id, destination, client);
        if (missing == "revoked")
        {
            await LoseAuthorityAsync(actor, missing);
        }
        if (missing == "expired")
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        }
        return actor;
    }

    private async Task LoseAuthorityAsync(Actor actor, string loss)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        switch (loss)
        {
            case "revoked":
                await db.Sessions.Where(session => session.Id == actor.SessionId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAt,
                        fixture.Clock.GetUtcNow()), Token);
                break;
            case "inactive":
                await fixture.SetIdentityActiveAsync(actor.IdentityId, false, Token);
                break;
            case "no-responsibility":
                await db.ResponsibilityAssignments.Where(assignment => assignment.IdentityId == actor.IdentityId)
                    .ExecuteDeleteAsync(Token);
                break;
            case "no-enablement":
                await fixture.RevokeEnablementAsync(actor.IdentityId, actor.Destination, Token);
                break;
            default:
                throw new ArgumentException("Unknown test authority change.", nameof(loss));
        }
    }

    private async Task<SseConnection> OpenAsync(HttpClient client, string query)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/notifications/stream?{query}");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(10), Token);
            response.EnsureSuccessStatusCode();
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Token));
            return new SseConnection(response, reader, cancellation);
        }
        catch
        {
            await cancellation.CancelAsync();
            cancellation.Dispose();
            throw;
        }
    }

    private void Publish(Guid destination) => fixture.Services.GetRequiredService<IChangeNotificationPublisher>()
        .Publish(new ChangeNotification(ChangeNotificationScope.PreparationDestination(destination)));

    private async Task WaitForCleanupAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (Hub.SubscriptionCount != 0)
        {
            await Task.Delay(10, deadline.Token);
        }
        Assert.Equal(0, Hub.SubscriptionCount);
    }

    private async Task<string?> ProblemCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString();

    private static string Scope(Guid destination) => $"scope=preparation.destination:{destination:D}";
    private sealed record Actor(Guid IdentityId, Guid SessionId, Guid Destination, HttpClient Client);

    private sealed class SseConnection(
        HttpResponseMessage response, StreamReader reader, CancellationTokenSource cancellation) : IAsyncDisposable
    {
        internal HttpResponseMessage Response => response;
        internal void Cancel() => cancellation.Cancel();

        internal async Task<string?> FrameAsync(CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var frame = "";
            while (true)
            {
                var line = await reader.ReadLineAsync(deadline.Token);
                if (line is null) return null;
                if (line.Length == 0) return frame;
                frame += line + "\n";
            }
        }

        internal async Task<string> InvalidationAsync(CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (await FrameAsync(deadline.Token) is { } frame)
            {
                if (frame.StartsWith("event:", StringComparison.Ordinal)) return frame;
            }
            throw new InvalidOperationException("Stream ended before invalidation.");
        }

        internal async Task AssertEndedWithoutInvalidationAsync(CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (await FrameAsync(deadline.Token) is { } frame)
            {
                Assert.StartsWith(":", frame);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync();
            reader.Dispose();
            response.Dispose();
            cancellation.Dispose();
        }
    }
}
