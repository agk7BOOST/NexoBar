using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Host.Notifications;
using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class OrderInvalidationTests(OrderOperationsApiFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pending_start_discard_and_subsequent_confirmation_publish_once_after_commit_and_not_replay()
    {
        await fixture.ResetAsync(Token);
        var order = await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("5", 2)], Token);
        var orderId = Guid.Parse(order.OperationalReference);
        var product = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token));
        var secondProduct = await fixture.CreateProductAsync("Second content", "5", Token);
        var notifications = new Recorder();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, Token, application);
        var pendingPath = $"/api/orders/{orderId}/pending-composition";
        var body = await AssertCommitAndReplayAsync(client, notifications, orderId, pendingPath, null, "pending_composition_commands");
        var pending = JsonSerializer.Deserialize<PendingCompositionResponse>(body, JsonSerializerOptions.Web)!;
        await AssertCommitAndReplayAsync(client, notifications, orderId,
            $"{pendingPath}/{pending.PendingCompositionId}/discard", null, "pending_composition_commands");
        body = await AssertCommitAndReplayAsync(client, notifications, orderId, pendingPath, null, "pending_composition_commands");
        pending = JsonSerializer.Deserialize<PendingCompositionResponse>(body, JsonSerializerOptions.Web)!;
        // Two new Contents and consuming the marker are one logical Order change.
        await AssertCommitAndReplayAsync(client, notifications, orderId,
            $"/api/order-operations/orders/{orderId}/confirmations",
            new SubsequentConfirmationRequest(pending.PendingCompositionId,
                [new SubsequentConfirmationItemRequest(product.ProductId, 1), new SubsequentConfirmationItemRequest(secondProduct.Id, 2)]),
            "subsequent_confirmation_commands");
    }

    [Fact]
    public async Task Preparation_progress_corrections_and_intervention_publish_both_exact_scopes()
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 6, 0, Token);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        var actor = await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, Token);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
            db.ResponsibilityAssignments.Add(new(actor.IdentityId, FunctionalResponsibility.OperationalIntervention));
            await db.SaveChangesAsync(Token);
        }
        var notifications = new Recorder();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(actor, Token, application);
        foreach (var (command, quantity) in new[] { ("start", 4), ("ready", 2), ("correct-start", 1), ("correct-ready", 1) })
            await AssertCommitAndReplayAsync(client, notifications, Guid.Parse(target.OperationalReference),
                $"/api/order-operations/preparation/work/{work.Id}/{command}", new { quantity },
                "preparation_commands", destinations: [work.PreparationResponsibilityId]);
        await AssertCommitAndReplayAsync(client, notifications, Guid.Parse(target.OperationalReference),
            $"/api/order-operations/intervention/work/{work.Id}/in-preparation", new { quantity = 1 },
            "preparation_commands", destinations: [work.PreparationResponsibilityId]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Content_and_delivery_changes_publish_once_for_exact_Order(bool prepared)
    {
        await fixture.ResetAsync(Token);
        var target = prepared
            ? await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 6, 2, Token)
            : await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 6, Token);
        var destinations = (await fixture.ReadPreparationWorkAsync(Token)).Select(x => x.PreparationResponsibilityId).ToArray();
        var other = await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("5", 1)], Token);
        var notifications = new Recorder();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, Token, application);
        var orderId = Guid.Parse(target.OperationalReference);
        var contentPath = $"/api/order-operations/orders/{orderId}/incorporations/{target.IncorporationId}/contents/{target.ContentOrdinal}";
        await AssertCommitAndReplayAsync(client, notifications, orderId,
            contentPath + "/correct-content-quantity", new { quantity = 1 }, "content_correction_commands", destinations: destinations);
        await AssertCommitAndReplayAsync(client, notifications, orderId,
            contentPath + "/cancel-content-quantity", new { quantity = 1 }, "content_cancellation_commands", destinations: destinations);
        await AssertCommitAndReplayAsync(client, notifications, orderId,
            $"/api/order-operations/incorporations/{target.IncorporationId}/contents/{target.ContentOrdinal}/deliver",
            new { quantity = 1 }, "delivery_commands", destinations: destinations);
        await AssertCommitAndReplayAsync(client, notifications, orderId,
            contentPath + "/correct-delivery", new { quantity = 1 }, "delivery_correction_commands", destinations: destinations);
        Assert.DoesNotContain(notifications.Events, x => x.Scope == ChangeNotificationScope.ActiveOrder(Guid.Parse(other.OperationalReference)));
    }

    [Theory]
    [InlineData("liquidate-simple")]
    [InlineData("record-external-collection")]
    public async Task Applied_price_then_Liquidation_are_normal_and_only_Closure_is_final(string liquidation)
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, Token);
        var orderId = Guid.Parse(target.OperationalReference);
        var content = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token));
        var notifications = new Recorder();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, Token, application);
        var pricePath = $"/api/order-operations/orders/{orderId}/incorporations/{target.IncorporationId}/contents/{target.ContentOrdinal}/apply-current-catalog-price";
        using (var noOp = await PostAsync(client, pricePath, new { }, Guid.NewGuid()))
            Assert.Equal(HttpStatusCode.Conflict, noOp.StatusCode);
        Assert.Empty(notifications.Events);
        // Existing fixture-style Catalog setup; no production Catalog freshness is involved.
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(Token);
            await using var command = new NpgsqlCommand("UPDATE catalog.products SET price = 8 WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", content.ProductId);
            await command.ExecuteNonQueryAsync(Token);
        }
        await AssertCommitAndReplayAsync(client, notifications, orderId, pricePath, new { }, "applied_price_correction_commands");
        await fixture.SetAllDeliveredQuantitiesAsync(orderId, Token);
        await AssertCommitAndReplayAsync(client, notifications, orderId,
            $"/api/order-operations/orders/{orderId}/{liquidation}",
            liquidation == "liquidate-simple" ? new { declaredPaymentMedium = "Cash" } : null, "liquidation_commands");
        await AssertCommitAndReplayAsync(client, notifications, orderId,
            $"/api/orders/{orderId}/close", null, "closure_commands", terminal: true);
    }

    [Fact]
    public async Task Complete_cancellation_dedupes_multiple_Contents_and_destinations_and_publishes_one_final()
    {
        await fixture.ResetAsync(Token);
        var destinationA = Guid.NewGuid();
        var destinationB = Guid.NewGuid();
        var productA = await fixture.CreateProductAsync("A", "5", Token);
        var secondProductA = await fixture.CreateProductAsync("A2", "5", Token);
        var productB = await fixture.CreateProductAsync("B", "5", Token);
        await fixture.SetProductPreparationAsync(productA.Id, destinationA, Token);
        await fixture.SetProductPreparationAsync(secondProductA.Id, destinationA, Token);
        await fixture.SetProductPreparationAsync(productB.Id, destinationB, Token);
        using var first = await PostAsync(fixture.OrderOperationsClient, "/api/order-operations/first-confirmations",
            new FirstConfirmationRequest("Multiple", [new(productA.Id, 1), new(secondProductA.Id, 2), new(productB.Id, 1)]), Guid.NewGuid());
        first.EnsureSuccessStatusCode();
        var order = (await first.Content.ReadFromJsonAsync<FirstConfirmationResponse>(Token))!;
        await fixture.StartPendingCompositionAsync(order.OperationalReference, Token);
        var notifications = new Recorder();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, Token, application);
        await AssertCommitAndReplayAsync(client, notifications, Guid.Parse(order.OperationalReference),
            $"/api/orders/{order.OperationalReference}/complete-cancellation", null, "complete_cancellation_commands",
            terminal: true, destinations: [destinationA, destinationB]);
    }

    [Fact]
    public async Task Rejection_authorization_conflict_and_rollback_are_silent()
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 2, 0, Token);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        var actor = await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, Token);
        var notifications = new Recorder();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(actor, Token, application);
        var path = $"/api/order-operations/preparation/work/{work.Id}/start";
        using (var rejected = await PostAsync(client, path, new { quantity = 3 }, Guid.NewGuid()))
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        using (var forbidden = await PostAsync(client, $"/api/orders/{target.OperationalReference}/pending-composition", null, Guid.NewGuid()))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await fixture.SetPreparationCommandFailureAsync(true, Token);
        try
        {
            using var failed = await PostAsync(client, path, new { quantity = 1 }, Guid.NewGuid());
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }
        finally { await fixture.SetPreparationCommandFailureAsync(false, Token); }
        Assert.Empty(notifications.Events);
        Assert.Equal(2, Assert.Single(await fixture.ReadPreparationWorkAsync(Token)).PendingQuantity);
        Assert.Empty(await fixture.ReadPreparationCommandsAsync(Token));
        var key = Guid.NewGuid();
        using (var success = await PostAsync(client, path, new { quantity = 1 }, key))
            success.EnsureSuccessStatusCode();
        notifications.Clear();
        using (var conflict = await PostAsync(client, path, new { quantity = 2 }, key))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Effective_delivery_rejects_complete_cancellation_without_publication()
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, Token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 1, Token);
        var notifications = new Recorder();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, Token, application);
        using var rejected = await PostAsync(client, $"/api/orders/{target.OperationalReference}/complete-cancellation", null, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Empty(notifications.Events);
    }

    [Theory]
    [InlineData("order.changed", false)]
    [InlineData("preparation.destination.changed", false)]
    [InlineData("order.changed", true)]
    [InlineData("preparation.destination.changed", true)]
    public async Task Publisher_failure_preserves_commit_and_attempts_other_freshness(string failingKind, bool terminal)
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 2, 0, Token);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        var actor = terminal ? fixture.DefaultOrderOperationsActor
            : await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, Token);
        var notifications = new Recorder { FailingKind = failingKind };
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(actor, Token, application);
        await AssertCommitAndReplayAsync(client, notifications, Guid.Parse(target.OperationalReference),
            terminal ? $"/api/orders/{target.OperationalReference}/complete-cancellation"
                : $"/api/order-operations/preparation/work/{work.Id}/start",
            terminal ? null : new { quantity = 1 },
            terminal ? "complete_cancellation_commands" : "preparation_commands", terminal,
            [work.PreparationResponsibilityId]);
        var persisted = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        Assert.Equal(terminal ? 0 : 1, persisted.PendingQuantity);
        Assert.Equal(terminal ? 0 : 1, persisted.InPreparationQuantity);
    }

    private async Task<string> AssertCommitAndReplayAsync(HttpClient client, Recorder recorder, Guid orderId,
        string path, object? body, string commandTable, bool terminal = false, Guid[]? destinations = null)
    {
        recorder.Clear();
        var key = Guid.NewGuid();
        // A separate connection must see the durable command at publication time, not
        // merely the service's saved-but-uncommitted tracked State. Never assert inside
        // the publisher: its exceptions are intentionally contained by the adapter.
        recorder.IsCommitted = () =>
        {
            using var connection = new NpgsqlConnection(fixture.ConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM order_operations.{commandTable} WHERE idempotency_key = @key)", connection);
            command.Parameters.AddWithValue("key", key);
            return (bool)command.ExecuteScalar()!;
        };
        using var fresh = await PostAsync(client, path, body, key);
        fresh.EnsureSuccessStatusCode();
        var response = await fresh.Content.ReadAsStringAsync(Token);
        var order = Assert.Single(recorder.Events, x => x.Kind == "order.changed");
        Assert.Equal(ChangeNotificationScope.ActiveOrder(orderId), order.Scope);
        Assert.Equal(terminal ? ChangeNotificationDelivery.FinalForPreviouslyAuthorizedScope : ChangeNotificationDelivery.Normal, order.Delivery);
        Assert.Equal((destinations ?? []).Order(), recorder.Events.Where(x => x.Kind == "preparation.destination.changed").Select(x => x.Scope.DestinationId).Order());
        Assert.Equal(1 + (destinations?.Length ?? 0), recorder.Events.Count);
        Assert.All(recorder.Committed, observed => Assert.True(observed));
        var count = recorder.Events.Count;
        using var replay = await PostAsync(client, path, body, key);
        replay.EnsureSuccessStatusCode();
        Assert.Equal(response, await replay.Content.ReadAsStringAsync(Token));
        Assert.Equal(count, recorder.Events.Count);
        recorder.IsCommitted = null;
        return response;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body, Guid key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);
    }

    private sealed class Recorder : IChangeNotificationPublisher
    {
        internal List<ChangeNotification> Events { get; } = [];
        internal List<bool> Committed { get; } = [];
        internal Func<bool>? IsCommitted { get; set; }
        internal string? FailingKind { get; init; }
        public void Publish(ChangeNotification notification)
        {
            Events.Add(notification);
            Committed.Add(false);
            Committed[^1] = IsCommitted?.Invoke() ?? true;
            if (notification.Kind == FailingKind) throw new InvalidOperationException("Simulated publisher failure.");
        }
        internal void Clear() { Events.Clear(); Committed.Clear(); }
    }
}
