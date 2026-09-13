using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexoBar.Host.Notifications;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ActiveOrderSseTests(OrderOperationsApiFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private ChangeNotificationHub Hub => fixture.Services.GetRequiredService<ChangeNotificationHub>();
    private static string OrderScope(Guid id) => $"scope=order.active:{id:D}";
    private static string PreparationScope(Guid id) => $"scope=preparation.destination:{id:D}";
    private static string Url(string scopes) => $"/api/notifications/stream?{scopes}";

    [Theory]
    [InlineData("none", HttpStatusCode.Forbidden)]
    [InlineData("intervention", HttpStatusCode.Forbidden)]
    [InlineData("preparation", HttpStatusCode.Forbidden)]
    [InlineData("inactive", HttpStatusCode.Unauthorized)]
    [InlineData("no-session", HttpStatusCode.Unauthorized)]
    [InlineData("revoked", HttpStatusCode.Unauthorized)]
    public async Task Initial_actor_authority_is_current_and_existence_safe(string authority, HttpStatusCode expected)
    {
        var actor = await ArrangeAsync(authority);
        using var client = actor.Client;
        if (authority is "inactive" or "revoked") await LoseAuthorityAsync(actor, authority);
        var reader = authority == "no-session" ? fixture.Client : client;
        foreach (var id in new[] { actor.OrderId, Guid.NewGuid() })
        {
            using var response = await reader.GetAsync(Url(OrderScope(id)), Token);
            Assert.Equal(expected, response.StatusCode);
            Assert.DoesNotContain(id.ToString(), await response.Content.ReadAsStringAsync(Token));
        }
        Assert.Equal(0, Hub.SubscriptionCount);
    }

    [Theory]
    [InlineData("active", true)]
    [InlineData("zero", true)]
    [InlineData("frozen", true)]
    [InlineData("closed", false)]
    [InlineData("cancelled", false)]
    [InlineData("missing", false)]
    public async Task Initial_scope_reuses_active_read_lifecycle(string lifecycle, bool readable)
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        await SetLifecycleAsync(actor, lifecycle);
        var id = lifecycle == "missing" ? Guid.NewGuid() : actor.OrderId;
        if (readable)
        {
            await using var stream = await OpenAsync(client, OrderScope(id));
            Assert.Equal(1, Hub.SubscriptionCount);
            Publish(id);
            Assert.Equal(OrderFrame(id), await stream.InvalidationAsync());
        }
        else
        {
            using var response = await client.GetAsync(Url(OrderScope(id)) + "&delivery=FinalForPreviouslyAuthorizedScope", Token);
            using var missing = await client.GetAsync(Url(OrderScope(Guid.NewGuid())), Token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var missingProblem = JsonDocument.Parse(await missing.Content.ReadAsStringAsync(Token));
            using var terminalProblem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            Assert.Equal(
                missingProblem.RootElement.EnumerateObject().Where(x => x.Name != "traceId").Select(x => (x.Name, x.Value.ToString())),
                terminalProblem.RootElement.EnumerateObject().Where(x => x.Name != "traceId").Select(x => (x.Name, x.Value.ToString())));
            var body = await response.Content.ReadAsStringAsync(Token);
            Assert.DoesNotContain(id.ToString(), body);
            Assert.Contains("order_operations.order.not_found", body);
        }
        await WaitForCleanupAsync();
    }

    [Theory]
    [InlineData("order.active:not-a-uuid")]
    [InlineData("order.active:00000000-0000-0000-0000-000000000000")]
    [InlineData("order.changed:11111111-1111-1111-1111-111111111111")]
    [InlineData("order.history:11111111-1111-1111-1111-111111111111")]
    [InlineData("order.active:11111111-1111-1111-1111-111111111111:final")]
    public async Task Invalid_or_client_defined_scope_is_rejected(string value)
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        using var response = await client.GetAsync(Url($"scope={value}"), Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, Hub.SubscriptionCount);
    }

    [Fact]
    public async Task Mixed_snapshot_routes_exact_scopes_deduplicates_and_disconnects_cleanly()
    {
        var actor = await ArrangeAsync("mixed");
        using var client = actor.Client;
        var other = await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("5", 2)], Token);
        var otherId = Guid.Parse(other.OperationalReference);
        await using (var mixed = await OpenAsync(client, $"{OrderScope(actor.OrderId)}&{OrderScope(actor.OrderId)}&{PreparationScope(actor.Destination)}"))
        await using (var second = await OpenAsync(client, OrderScope(otherId)))
        await using (var preparation = await OpenAsync(client, PreparationScope(actor.Destination)))
        {
            Publish(actor.OrderId);
            Assert.Equal(OrderFrame(actor.OrderId), await mixed.InvalidationAsync());
            Assert.Equal(": keep-alive\n", await mixed.FrameAsync()); // No duplicate from duplicate scope.
            Assert.Equal(": keep-alive\n", await second.FrameAsync());
            Assert.Equal(": keep-alive\n", await preparation.FrameAsync());
            Publish(otherId);
            Assert.Equal(OrderFrame(otherId), await second.InvalidationAsync());
            fixture.Services.GetRequiredService<IChangeNotificationPublisher>().Publish(
                new(ChangeNotificationScope.PreparationDestination(actor.Destination)));
            var frame = $"event: invalidation\ndata: {{\"kind\":\"preparation.destination.changed\",\"scopeId\":\"{actor.Destination:D}\"}}\n";
            Assert.Equal(frame, await mixed.InvalidationAsync());
            Assert.Equal(frame, await preparation.InvalidationAsync());
            Assert.Equal(3, Hub.SubscriptionCount);
        }
        await WaitForCleanupAsync();
    }

    [Theory]
    [InlineData("revoked", false)] [InlineData("revoked", true)]
    [InlineData("inactive", false)] [InlineData("inactive", true)]
    [InlineData("no-responsibility", false)] [InlineData("no-responsibility", true)]
    [InlineData("replaced", false)] [InlineData("replaced", true)]
    [InlineData("closed", false)] [InlineData("closed", true)]
    [InlineData("cancelled", false)] [InlineData("cancelled", true)]
    public async Task Normal_delivery_and_idle_revalidation_fail_closed(string loss, bool idle)
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        if (!idle) SetHeartbeat(TimeSpan.FromMinutes(1));
        await using var stream = await OpenAsync(client, OrderScope(actor.OrderId));
        if (loss is "closed" or "cancelled") await SetLifecycleAsync(actor, loss);
        else await LoseAuthorityAsync(actor, loss);
        if (!idle) Publish(actor.OrderId);
        await stream.AssertEndedAsync();
        await WaitForCleanupAsync();
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("cancelled")]
    public async Task Explicit_final_retires_only_exact_Order_and_preserves_mixed_delivery(string terminal)
    {
        var actor = await ArrangeAsync("mixed");
        using var client = actor.Client;
        var other = await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("5", 2)], Token);
        var otherId = Guid.Parse(other.OperationalReference);
        SetHeartbeat(TimeSpan.FromMinutes(1));
        await using var stream = await OpenAsync(client, $"{OrderScope(actor.OrderId)}&{PreparationScope(actor.Destination)}&{OrderScope(otherId)}");
        await SetLifecycleAsync(actor, terminal);
        using (var read = await client.GetAsync($"/api/order-operations/orders/{actor.OrderId}", Token))
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        // The same current read policy rejects normal notification authorization now.
        using (var reconnect = await client.GetAsync(Url(OrderScope(actor.OrderId)), Token))
            Assert.Equal(HttpStatusCode.NotFound, reconnect.StatusCode);
        Publish(actor.OrderId, final: true);
        Publish(actor.OrderId, final: true);
        Publish(actor.OrderId);
        Assert.Equal(OrderFrame(actor.OrderId), await stream.InvalidationAsync());
        var preparationFrame = $"event: invalidation\ndata: {{\"kind\":\"preparation.destination.changed\",\"scopeId\":\"{actor.Destination:D}\"}}\n";
        Hub.Publish(new(ChangeNotificationScope.PreparationDestination(actor.Destination)));
        Assert.Equal(preparationFrame, await stream.InvalidationAsync());
        // Reading Preparation proves retirement completed. Future publications must not
        // fill the shared buffer or deliver anything, including another final event.
        for (var count = 0; count <= ChangeNotificationHub.BufferCapacity; count++)
        {
            Publish(actor.OrderId);
            Publish(actor.OrderId, final: true);
        }
        Publish(otherId);
        Assert.Equal(OrderFrame(otherId), await stream.InvalidationAsync());
        Hub.Publish(new(ChangeNotificationScope.PreparationDestination(actor.Destination)));
        Assert.Equal(preparationFrame, await stream.InvalidationAsync());
        Assert.Equal(1, Hub.SubscriptionCount);
        using var after = await client.GetAsync(Url(OrderScope(actor.OrderId)), Token);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Theory]
    [InlineData("revoked", false)] [InlineData("revoked", true)]
    [InlineData("inactive", false)] [InlineData("inactive", true)]
    [InlineData("no-responsibility", false)] [InlineData("no-responsibility", true)]
    public async Task Retired_Order_keeps_heartbeat_but_actor_loss_ends_entire_connection(string loss, bool mixed)
    {
        var actor = await ArrangeAsync("mixed");
        using var client = actor.Client;
        await using var stream = await OpenAsync(client,
            OrderScope(actor.OrderId) + (mixed ? $"&{PreparationScope(actor.Destination)}" : ""));
        // Exercise the trusted classification before terminality so a heartbeat cannot
        // observe terminal State ahead of the final publication in this idle-check test.
        Publish(actor.OrderId, final: true);
        Assert.Equal(OrderFrame(actor.OrderId), await stream.InvalidationAsync());
        await SetLifecycleAsync(actor, "cancelled");
        Publish(actor.OrderId);
        Publish(actor.OrderId, final: true);
        Assert.Equal(": keep-alive\n", await stream.FrameAsync());
        Assert.Equal(1, Hub.SubscriptionCount);
        await LoseAuthorityAsync(actor, loss);
        if (mixed)
            Hub.Publish(new(ChangeNotificationScope.PreparationDestination(actor.Destination)));
        await stream.AssertEndedAsync();
        await WaitForCleanupAsync();
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("inactive")]
    [InlineData("no-responsibility")]
    [InlineData("replaced")]
    [InlineData("expired")]
    public async Task Final_exception_never_bypasses_current_actor_authority(string loss)
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        SetHeartbeat(TimeSpan.FromMinutes(1));
        await using var stream = await OpenAsync(client, OrderScope(actor.OrderId));
        await SetLifecycleAsync(actor, "cancelled");
        await LoseAuthorityAsync(actor, loss);
        Publish(actor.OrderId, final: true);
        await stream.AssertEndedAsync();
        await WaitForCleanupAsync();
    }

    [Fact]
    public async Task Final_cannot_reach_an_unsubscribed_Order_or_Preparation_only_stream()
    {
        var actor = await ArrangeAsync("mixed");
        using var client = actor.Client;
        var other = await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("5", 2)], Token);
        await using var stream = await OpenAsync(client, OrderScope(Guid.Parse(other.OperationalReference)));
        await using var preparation = await OpenAsync(client, PreparationScope(actor.Destination));
        await SetLifecycleAsync(actor, "cancelled");
        Publish(actor.OrderId, final: true);
        Assert.Equal(": keep-alive\n", await stream.FrameAsync());
        Assert.Equal(": keep-alive\n", await preparation.FrameAsync());
        Assert.Equal(2, Hub.SubscriptionCount);
    }

    [Fact]
    public async Task Order_authority_does_not_grant_Preparation_or_an_unauthorized_mixed_snapshot()
    {
        var actor = await ArrangeAsync();
        using var client = actor.Client;
        using var response = await client.GetAsync(Url($"{OrderScope(actor.OrderId)}&{PreparationScope(actor.Destination)}"), Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, Hub.SubscriptionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_exception_does_not_skip_other_scopes_current_authority(bool otherOrder)
    {
        var actor = await ArrangeAsync("mixed");
        using var client = actor.Client;
        var second = await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("5", 2)], Token);
        SetHeartbeat(TimeSpan.FromMinutes(1));
        await using var stream = await OpenAsync(client, $"{OrderScope(actor.OrderId)}&{(otherOrder ? OrderScope(Guid.Parse(second.OperationalReference)) : PreparationScope(actor.Destination))}");
        await SetLifecycleAsync(actor, "cancelled");
        if (otherOrder) await SetLifecycleAsync(actor with { Order = second }, "cancelled");
        else
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>().PreparationEnablements
                .Where(x => x.IdentityId == actor.IdentityId).ExecuteDeleteAsync(Token);
        }
        Publish(actor.OrderId, final: true);
        await stream.AssertEndedAsync();
        await WaitForCleanupAsync();
    }

    [Fact]
    public async Task Initial_reconnected_and_connected_validation_do_not_renew_Session()
    {
        var actor = await ArrangeAsync();
        using var originalClient = actor.Client;
        var clock = new PassiveClock();
        await using var application = fixture.CreateApplicationWithTimeProvider(clock);
        var identity = await fixture.CreateDeliveryActorAsync(true, false, null, Token);
        using var client = await fixture.LoginAsync(identity, Token, application);
        application.Services.GetRequiredService<IOptions<SseTransportOptions>>().Value.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
        var before = await SessionTimesAsync(identity.IdentityId);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            await using var stream = await OpenAsync(client, OrderScope(actor.OrderId));
            Assert.Equal(before, await SessionTimesAsync(identity.IdentityId));
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(": keep-alive\n", await stream.FrameAsync());
            application.Services.GetRequiredService<IChangeNotificationPublisher>().Publish(new(ChangeNotificationScope.ActiveOrder(actor.OrderId)));
            Assert.Equal(OrderFrame(actor.OrderId), await stream.InvalidationAsync());
            Assert.Equal(before, await SessionTimesAsync(identity.IdentityId));
        }
        await using var expiring = await OpenAsync(client, OrderScope(actor.OrderId));
        clock.Advance(TimeSpan.FromMinutes(30));
        await expiring.AssertEndedAsync();
        Assert.Equal(before, await SessionTimesAsync(identity.IdentityId));
    }

    private async Task<Actor> ArrangeAsync(string authority = "operations")
    {
        await WaitForCleanupAsync();
        await fixture.ResetAsync(Token);
        SetHeartbeat(TimeSpan.FromMilliseconds(100));
        var order = await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("5", 2)], Token);
        var destination = Guid.NewGuid();
        var identity = await fixture.CreateDeliveryActorAsync(authority is not ("none" or "intervention" or "preparation"),
            authority is "mixed" or "preparation", authority is "mixed" or "preparation" ? destination : null, Token);
        if (authority == "intervention")
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
            db.ResponsibilityAssignments.Add(new(identity.IdentityId, FunctionalResponsibility.OperationalIntervention));
            await db.SaveChangesAsync(Token);
        }
        return new(order, identity.IdentityId, destination, await fixture.LoginAsync(identity, Token));
    }

    private void SetHeartbeat(TimeSpan interval) => fixture.Services.GetRequiredService<IOptions<SseTransportOptions>>().Value.HeartbeatInterval = interval;
    private void Publish(Guid id, bool final = false) => fixture.Services.GetRequiredService<IChangeNotificationPublisher>().Publish(
        final ? ChangeNotification.FinalOrderInvalidation(id) : new(ChangeNotificationScope.ActiveOrder(id)));

    private async Task SetLifecycleAsync(Actor actor, string lifecycle)
    {
        // These transport tests explicitly control notification timing/classification.
        // Commit setup commands through an isolated publisher; publication semantics
        // of the production adapter are covered by OrderInvalidationTests.
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(new SetupPublisher());
        using var commandClient = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, Token, application);
        if (lifecycle is "closed" or "frozen")
        {
            await fixture.SetAllDeliveredQuantitiesAsync(actor.OrderId, Token);
            using var liquidation = await LiquidationTestSupport.PostExternalAsync(commandClient, actor.OrderId.ToString(), Guid.NewGuid(), Token);
            liquidation.EnsureSuccessStatusCode();
        }
        string? path = lifecycle switch
        {
            "closed" => $"/api/orders/{actor.OrderId}/close",
            "cancelled" => $"/api/orders/{actor.OrderId}/complete-cancellation",
            "zero" => $"/api/order-operations/orders/{actor.OrderId}/incorporations/{actor.Order.FirstIncorporation.Id}/contents/1/cancel-content-quantity",
            _ => null
        };
        if (path is null) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (lifecycle == "zero") request.Content = JsonContent.Create(new { quantity = 2 });
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(commandClient, request, Token);
        response.EnsureSuccessStatusCode();
    }

    private async Task LoseAuthorityAsync(Actor actor, string loss)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        if (loss == "revoked") await db.Sessions.Where(x => x.IdentityId == actor.IdentityId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), Token);
        if (loss == "expired") await db.Sessions.Where(x => x.IdentityId == actor.IdentityId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AbsoluteExpiresAt, DateTimeOffset.UtcNow), Token);
        if (loss == "inactive") await db.Identities.Where(x => x.Id == actor.IdentityId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), Token);
        if (loss == "no-responsibility") await fixture.RevokeOrderOperationsAssignmentAsync(actor.IdentityId, Token);
        if (loss == "replaced")
        {
            var next = await fixture.CreateDeliveryActorAsync(true, false, null, Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity-sessions")
            { Content = JsonContent.Create(new { loginIdentifier = next.LoginIdentifier, secret = next.Secret }) };
            using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(actor.Client, request, Token);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<(DateTimeOffset, DateTimeOffset)> SessionTimesAsync(Guid identityId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>().Sessions.AsNoTracking()
            .SingleAsync(x => x.IdentityId == identityId, Token);
        return (session.LastActivityAt, session.AbsoluteExpiresAt);
    }

    private async Task<Connection> OpenAsync(HttpClient client, string query)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        try
        {
            var response = await client.GetAsync(Url(query), HttpCompletionOption.ResponseHeadersRead, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            response.EnsureSuccessStatusCode();
            var stream = new Connection(response, new StreamReader(await response.Content.ReadAsStreamAsync(Token)), cancellation);
            Assert.Equal(": connected\n", await stream.FrameAsync());
            return stream;
        }
        catch
        {
            await cancellation.CancelAsync();
            cancellation.Dispose();
            throw;
        }
    }

    private async Task WaitForCleanupAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (Hub.SubscriptionCount != 0) await Task.Delay(10, deadline.Token);
    }

    private static string OrderFrame(Guid id) => $"event: invalidation\ndata: {{\"kind\":\"order.changed\",\"scopeId\":\"{id:D}\"}}\n";
    private sealed record Actor(FirstConfirmationResponse Order, Guid IdentityId, Guid Destination, HttpClient Client)
    { internal Guid OrderId => Guid.Parse(Order.OperationalReference); }
    private sealed class SetupPublisher : IChangeNotificationPublisher
    {
        public void Publish(ChangeNotification notification) { }
    }
    private sealed class PassiveClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class Connection(HttpResponseMessage response, StreamReader reader, CancellationTokenSource cancellation) : IAsyncDisposable
    {
        internal async Task<string?> FrameAsync(CancellationToken? token = null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token ?? Token);
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
        internal async Task<string> InvalidationAsync()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (await FrameAsync(deadline.Token) is { } frame) if (frame.StartsWith("event:", StringComparison.Ordinal)) return frame;
            throw new InvalidOperationException("Stream ended before invalidation.");
        }
        internal async Task AssertEndedAsync()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (await FrameAsync(deadline.Token) is { } frame) Assert.StartsWith(":", frame);
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

public sealed class ActiveOrderSseScopeTests
{
    [Fact]
    public void Scope_identity_includes_kind_and_Order_duplicates_are_deduplicated()
    {
        var id = Guid.NewGuid();
        Assert.True(ChangeNotificationScope.TryParse($"order.active:{id:D}", out var order));
        Assert.Equal(ChangeNotificationScope.ActiveOrder(id), order);
        Assert.NotEqual(ChangeNotificationScope.PreparationDestination(id), order);
        var hub = new ChangeNotificationHub();
        using var subscription = hub.Subscribe([order!, order!]);
        Assert.Equal(1, subscription.ScopeCount);
        hub.Publish(new(ChangeNotificationScope.PreparationDestination(id)));
        hub.Publish(ChangeNotification.FinalOrderInvalidation(Guid.NewGuid()));
        Assert.False(subscription.Reader.TryRead(out _));
        hub.Publish(new(order!));
        Assert.True(subscription.Reader.TryRead(out var notification));
        Assert.Equal("order.changed", notification.Kind);
        Assert.Equal(ChangeNotificationDelivery.Normal, notification.Delivery);
    }
}
