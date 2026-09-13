using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexoBar.Host.Notifications;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class InventoryOperationSseTests(IdentitiesAndCapabilitiesFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private ChangeNotificationHub Hub => fixture.Services.GetRequiredService<ChangeNotificationHub>();

    [Fact]
    public void Static_scope_parses_deduplicates_and_coexists_with_keyed_scopes()
    {
        var destinationId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        Assert.True(ChangeNotificationScope.TryParse("inventory.operation", out var inventory));
        Assert.Equal(ChangeNotificationScope.InventoryOperation(), inventory);
        Assert.True(ChangeNotificationScope.TryParse($"preparation.destination:{destinationId:D}", out var preparation));
        Assert.True(ChangeNotificationScope.TryParse($"order.active:{orderId:D}", out var order));
        using var subscription = Hub.Subscribe([inventory!, inventory!, preparation!, order!]);
        Assert.Equal(3, subscription.ScopeCount);

        Hub.Publish(new ChangeNotification(ChangeNotificationScope.InventoryOperation()));
        Hub.Publish(new ChangeNotification(preparation!));
        Hub.Publish(new ChangeNotification(order!));
        Assert.True(subscription.Reader.TryRead(out var inventoryNotification));
        Assert.Equal("inventory.operation.changed", inventoryNotification!.Kind);
        Assert.Null(inventoryNotification.Scope.ScopeId);
        Assert.True(subscription.Reader.TryRead(out _));
        Assert.True(subscription.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("inventory.operation:11111111-1111-1111-1111-111111111111")]
    [InlineData("inventory")]
    [InlineData("inventory.operation.extra")]
    public void Static_scope_rejects_malformed_variants(string value) =>
        Assert.False(ChangeNotificationScope.TryParse(value, out _));

    [Fact]
    public async Task Scope_count_remains_bounded_and_duplicate_static_scope_uses_one_subscription()
    {
        var actor = await ArrangeAsync("operation");
        using var client = actor.Client;
        var tooMany = string.Join('&', Enumerable.Repeat(Scope, SseTransport.MaximumScopes + 1));
        using var rejected = await client.GetAsync($"/api/notifications/stream?{tooMany}", Token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        await using var stream = await OpenAsync(client, $"{Scope}&{Scope}");
        Assert.Equal(": connected\n", await stream.FrameAsync());
        Publish();
        Assert.Equal(InventoryFrame, await stream.InvalidationAsync());
        Assert.Equal(1, Hub.SubscriptionCount);
    }

    [Theory]
    [InlineData("no-session", HttpStatusCode.Unauthorized)]
    [InlineData("inactive", HttpStatusCode.Unauthorized)]
    [InlineData("configuration", HttpStatusCode.Forbidden)]
    [InlineData("unrelated", HttpStatusCode.Forbidden)]
    public async Task Initial_authorization_requires_current_InventoryOperation(
        string authority, HttpStatusCode expected)
    {
        var actor = await ArrangeAsync(authority);
        using var client = actor.Client;
        using var response = await client.GetAsync($"/api/notifications/stream?{Scope}", Token);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, Hub.SubscriptionCount);
    }

    [Fact]
    public async Task Inventory_operation_authorization_emits_an_opaque_static_invalidation()
    {
        var actor = await ArrangeAsync("operation");
        using var client = actor.Client;
        await using var stream = await OpenAsync(client, Scope);
        Assert.Equal(": connected\n", await stream.FrameAsync());
        Publish();
        Assert.Equal(InventoryFrame, await stream.InvalidationAsync());
    }

    [Fact]
    public void Hub_filters_static_inventory_notifications_and_mixed_snapshots_independently()
    {
        var preparation = ChangeNotificationScope.PreparationDestination(Guid.NewGuid());
        var order = ChangeNotificationScope.ActiveOrder(Guid.NewGuid());
        var inventory = ChangeNotificationScope.InventoryOperation();
        using var inventoryOnly = Hub.Subscribe([inventory]);
        using var preparationOnly = Hub.Subscribe([preparation]);
        using var orderOnly = Hub.Subscribe([order]);
        using var mixed = Hub.Subscribe([preparation, order, inventory]);

        Hub.Publish(new ChangeNotification(inventory));
        Assert.True(inventoryOnly.Reader.TryRead(out var inventoryNotification));
        Assert.Equal("inventory.operation.changed", inventoryNotification!.Kind);
        Assert.False(preparationOnly.Reader.TryRead(out _));
        Assert.False(orderOnly.Reader.TryRead(out _));
        Assert.True(mixed.Reader.TryRead(out var mixedInventory));
        Assert.Equal("inventory.operation.changed", mixedInventory!.Kind);

        Hub.Publish(new ChangeNotification(preparation));
        Hub.Publish(new ChangeNotification(order));
        Assert.True(mixed.Reader.TryRead(out var mixedPreparation));
        Assert.Equal("preparation.destination.changed", mixedPreparation!.Kind);
        Assert.True(mixed.Reader.TryRead(out var mixedOrder));
        Assert.Equal("order.changed", mixedOrder!.Kind);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("inactive")]
    [InlineData("operation-removed")]
    public async Task Connected_delivery_revalidates_current_InventoryOperation(string loss)
    {
        var actor = await ArrangeAsync("operation-and-configuration");
        using var client = actor.Client;
        fixture.Services.GetRequiredService<IOptions<SseTransportOptions>>().Value.HeartbeatInterval = TimeSpan.FromMinutes(1);
        await using var stream = await OpenAsync(client, Scope);
        Assert.Equal(": connected\n", await stream.FrameAsync());
        await LoseAuthorityAsync(actor, loss);
        Publish();
        Assert.Null(await stream.FrameAsync());
        await WaitForCleanupAsync();
    }

    [Fact]
    public async Task Opening_heartbeat_and_delivery_do_not_renew_session_inactivity()
    {
        var actor = await ArrangeAsync("operation");
        using var client = actor.Client;
        var original = Assert.Single(await fixture.ReadSessionsAsync(Token));
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await using var stream = await OpenAsync(client, Scope);
        Assert.Equal(": connected\n", await stream.FrameAsync());
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(": keep-alive\n", await stream.FrameAsync());
        Publish();
        Assert.Equal(InventoryFrame, await stream.InvalidationAsync());
        var current = Assert.Single(await fixture.ReadSessionsAsync(Token));
        Assert.Equal(original.LastActivityAt, current.LastActivityAt);
        Assert.Equal(original.AbsoluteExpiresAt, current.AbsoluteExpiresAt);
    }

    [Fact]
    public async Task Disconnect_cleans_the_static_inventory_subscription()
    {
        var actor = await ArrangeAsync("operation");
        using var client = actor.Client;
        var stream = await OpenAsync(client, Scope);
        Assert.Equal(1, Hub.SubscriptionCount);
        await stream.DisposeAsync();
        await WaitForCleanupAsync();
    }

    private async Task<Actor> ArrangeAsync(string authority)
    {
        await WaitForCleanupAsync();
        await fixture.ResetAsync(Token);
        fixture.Services.GetRequiredService<IOptions<SseTransportOptions>>().Value.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
        var identity = await fixture.CreateIdentityAsync("Inventory SSE actor", authority != "inactive", Token);
        if (authority is "operation" or "operation-and-configuration")
        {
            await fixture.InsertAssignmentAsync(identity.Id, FunctionalResponsibility.InventoryOperation, Token);
        }
        if (authority is "configuration" or "operation-and-configuration")
        {
            await fixture.InsertAssignmentAsync(identity.Id, FunctionalResponsibility.InventoryConfiguration, Token);
        }
        if (authority == "unrelated")
        {
            await fixture.InsertAssignmentAsync(identity.Id, FunctionalResponsibility.CatalogConfiguration, Token);
        }

        var generated = SessionToken.Generate();
        var now = fixture.Clock.GetUtcNow();
        var session = new IdentitySession(identity.Id, generated.TokenHash, now, now + TimeSpan.FromHours(12));
        await fixture.InsertSessionAsync(session, Token);
        var client = fixture.CreateClient();
        if (authority != "no-session")
        {
            client.DefaultRequestHeaders.Add("Cookie", $"nexobar-session-test={generated.RawToken}");
        }
        return new Actor(identity.Id, session.Id, client);
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
            case "operation-removed":
                await db.ResponsibilityAssignments.Where(assignment =>
                        assignment.IdentityId == actor.IdentityId &&
                        assignment.ResponsibilityCode == FunctionalResponsibility.InventoryOperation)
                    .ExecuteDeleteAsync(Token);
                break;
            default:
                throw new ArgumentException("Unknown authority loss.", nameof(loss));
        }
    }

    private async Task<SseConnection> OpenAsync(HttpClient client, string query)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        try
        {
            var response = await client.GetAsync($"/api/notifications/stream?{query}",
                    HttpCompletionOption.ResponseHeadersRead, cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(10), Token);
            response.EnsureSuccessStatusCode();
            return new SseConnection(response,
                new StreamReader(await response.Content.ReadAsStreamAsync(Token)), cancellation);
        }
        catch
        {
            await cancellation.CancelAsync();
            cancellation.Dispose();
            throw;
        }
    }

    private void Publish() => fixture.Services.GetRequiredService<IChangeNotificationPublisher>()
        .Publish(new ChangeNotification(ChangeNotificationScope.InventoryOperation()));

    private async Task WaitForCleanupAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (Hub.SubscriptionCount != 0)
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private const string Scope = "scope=inventory.operation";
    private const string InventoryFrame = "event: invalidation\ndata: {\"kind\":\"inventory.operation.changed\"}\n";
    private sealed record Actor(Guid IdentityId, Guid SessionId, HttpClient Client);

    private sealed class SseConnection(
        HttpResponseMessage response,
        StreamReader reader,
        CancellationTokenSource cancellation) : IAsyncDisposable
    {
        internal async Task<string?> FrameAsync()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
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
            while (await FrameAsync() is { } frame)
            {
                if (frame.StartsWith("event:", StringComparison.Ordinal)) return frame;
            }
            throw new InvalidOperationException("Stream ended before invalidation.");
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
