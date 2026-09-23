using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NexoBar.Host.Notifications;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OperationalConfiguration;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class OrderContextChangeTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Context_change_sequence_preserves_order_content_and_exact_replay_does_not_reapply()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var a = await fixture.EnsureConfiguredTestContextAsync("Context A", token);
        var b = await fixture.EnsureConfiguredTestContextAsync("Context B", token);
        var c = await fixture.EnsureConfiguredTestContextAsync("Context C", token);
        var product = await fixture.CreateProductAsync("Context change product", "12.50", token);
        var firstConfirmationKey = Guid.NewGuid();
        using var client = await fixture.LoginWithoutLegacyRequestAdapterAsync(
            fixture.DefaultOrderOperationsActor, token);
        using var first = await PostAsync(client, "/api/order-operations/first-confirmations",
            new FirstConfirmationRequest(a, null, [new FirstConfirmationItemRequest(product.Id, 2)]),
            firstConfirmationKey, token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var confirmation = Assert.IsType<FirstConfirmationResponse>(
            await first.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
        var orderId = Guid.Parse(confirmation.OperationalReference);
        var pending = await fixture.StartPendingCompositionAsync(confirmation.OperationalReference, token, client);

        var k1 = Guid.NewGuid();
        var abRequest = new OrderContextChangeRequest(a, b);
        using var ab = await PostAsync(client, $"/api/order-operations/orders/{orderId}/context-changes", abRequest, k1, token);
        Assert.Equal(HttpStatusCode.OK, ab.StatusCode);
        var originalAb = Assert.IsType<OrderContextChangeResponse>(await ab.Content.ReadFromJsonAsync<OrderContextChangeResponse>(token));
        Assert.Equal(a, originalAb.PreviousContextId);
        Assert.Equal(b, originalAb.CurrentContextId);
        Assert.Equal("Context A", originalAb.PreviousContextOperationalName);
        Assert.Equal("Context B", originalAb.CurrentContextOperationalName);

        using var bc = await PostAsync(client, $"/api/order-operations/orders/{orderId}/context-changes",
            new OrderContextChangeRequest(b, c), Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.OK, bc.StatusCode);
        using var ca = await PostAsync(client, $"/api/order-operations/orders/{orderId}/context-changes",
            new OrderContextChangeRequest(c, a), Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.OK, ca.StatusCode);
        using var replay = await PostAsync(client, $"/api/order-operations/orders/{orderId}/context-changes", abRequest, k1, token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = Assert.IsType<OrderContextChangeResponse>(await replay.Content.ReadFromJsonAsync<OrderContextChangeResponse>(token));
        Assert.Equal(originalAb, replayed);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var order = await db.Orders.SingleAsync(x => x.Id == orderId, token);
        Assert.Equal(a, order.CurrentContextId);
        Assert.Equal("Context A", order.CurrentContextOperationalName);
        var changes = await db.OrderContextChangeHistory.Where(x => x.OrderId == orderId)
            .OrderBy(x => x.Sequence).ToArrayAsync(token);
        Assert.Equal(3, changes.Length);
        Assert.Equal(new[] { 1, 2, 3 }, changes.Select(x => x.Sequence));
        Assert.Equal((a, b, "Context A", "Context B"),
            (changes[0].PreviousContextId, changes[0].NewContextId,
                changes[0].PreviousContextOperationalName, changes[0].NewContextOperationalName));
        Assert.Equal((b, c, "Context B", "Context C"),
            (changes[1].PreviousContextId, changes[1].NewContextId,
                changes[1].PreviousContextOperationalName, changes[1].NewContextOperationalName));
        Assert.Equal((c, a, "Context C", "Context A"),
            (changes[2].PreviousContextId, changes[2].NewContextId,
                changes[2].PreviousContextOperationalName, changes[2].NewContextOperationalName));
        Assert.Equal(1, await db.IncorporationContents.CountAsync(token));
        Assert.Equal(1, await db.Incorporations.CountAsync(token));
        var currentPending = await db.PendingCompositions.SingleAsync(x => x.OrderId == orderId, token);
        Assert.Equal(pending.PendingCompositionId, currentPending.Id);
        var confirmationHistory = await db.ConfirmationHistory.SingleAsync(x => x.IncorporationId == confirmation.FirstIncorporation.Id, token);
        Assert.Equal(a, confirmationHistory.ConfirmedContextId);
        Assert.Equal("Context A", confirmationHistory.ConfirmedContext);

        using var active = await client.GetAsync($"/api/order-operations/orders/{orderId}", token);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        var activeOrder = Assert.IsType<OrderQueryResponse>(await active.Content.ReadFromJsonAsync<OrderQueryResponse>(token));
        Assert.Equal(a, activeOrder.ContextId);
        Assert.Equal("Context A", activeOrder.Context);
        using var deliveryRead = await client.GetAsync($"/api/order-operations/orders/{orderId}/delivery", token);
        Assert.Equal(HttpStatusCode.OK, deliveryRead.StatusCode);
        var delivery = Assert.IsType<OrderDeliveryResponse>(await deliveryRead.Content.ReadFromJsonAsync<OrderDeliveryResponse>(token));
        Assert.Equal("Context A", delivery.CurrentContext);
        Assert.Single(delivery.Contents);

        await fixture.RevokeOrderOperationsAssignmentAsync(fixture.DefaultOrderOperationsActor.IdentityId, token);
        using var replayAfterRevocation = await PostAsync(client, $"/api/order-operations/orders/{orderId}/context-changes", abRequest, k1, token);
        Assert.Equal(HttpStatusCode.OK, replayAfterRevocation.StatusCode);
        using var changedTargetConflict = await PostAsync(client, $"/api/order-operations/orders/{orderId}/context-changes",
            new OrderContextChangeRequest(a, c), k1, token);
        Assert.Equal(HttpStatusCode.Conflict, changedTargetConflict.StatusCode);
        using var changedExpectedConflict = await PostAsync(client, $"/api/order-operations/orders/{orderId}/context-changes",
            new OrderContextChangeRequest(b, b), k1, token);
        Assert.Equal(HttpStatusCode.Conflict, changedExpectedConflict.StatusCode);
        using var wrongOrderConflict = await PostAsync(client, $"/api/order-operations/orders/{Guid.CreateVersion7()}/context-changes", abRequest, k1, token);
        Assert.Equal(HttpStatusCode.Conflict, wrongOrderConflict.StatusCode);
        var otherActor = await fixture.CreateDeliveryActorAsync(false, false, null, token);
        using var otherActorClient = await fixture.LoginWithoutLegacyRequestAdapterAsync(otherActor, token);
        using var wrongActorConflict = await PostAsync(otherActorClient, $"/api/order-operations/orders/{orderId}/context-changes", abRequest, k1, token);
        Assert.Equal(HttpStatusCode.Conflict, wrongActorConflict.StatusCode);

        Assert.False(await fixture.HasPendingModelChangesAsync());
        await fixture.MigrateOrderOperationsAsync("20260922140000_AddConfiguredOrderContexts", token);
        Assert.Equal(1, await db.Orders.CountAsync(x => x.Id == orderId, token));
        Assert.Equal("Context A", (await db.ConfirmationHistory.SingleAsync(x => x.IncorporationId == confirmation.FirstIncorporation.Id, token)).ConfirmedContext);
        await fixture.MigrateOrderOperationsAsync("20260922160000_AddProductOperationalNameSnapshot", token);
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    [Fact]
    public async Task No_change_stale_unknown_context_and_freeze_are_rejected_without_false_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var a = await fixture.EnsureConfiguredTestContextAsync("Context A", token);
        var b = await fixture.EnsureConfiguredTestContextAsync("Context B", token);
        var product = await fixture.CreateProductAsync("Context eligibility product", "4", token);
        using var client = await fixture.LoginWithoutLegacyRequestAdapterAsync(fixture.DefaultOrderOperationsActor, token);
        using var first = await PostAsync(client, "/api/order-operations/first-confirmations",
            new FirstConfirmationRequest(a, null, [new FirstConfirmationItemRequest(product.Id, 1)]), Guid.NewGuid(), token);
        var confirmation = Assert.IsType<FirstConfirmationResponse>(await first.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
        var orderId = Guid.Parse(confirmation.OperationalReference);
        var path = $"/api/order-operations/orders/{orderId}/context-changes";

        using var noChange = await PostAsync(client, path, new OrderContextChangeRequest(a, a), Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Conflict, noChange.StatusCode);
        Assert.Equal("order.context_change.no_change", await ReadCodeAsync(noChange, token));
        using var stale = await PostAsync(client, path, new OrderContextChangeRequest(b, a), Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("order.context_change.expected_context_stale", await ReadCodeAsync(stale, token));
        using var missing = await PostAsync(client, path, new OrderContextChangeRequest(a, Guid.CreateVersion7()), Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal("order.context_change.target_context_not_found", await ReadCodeAsync(missing, token));

        using var delivery = await PostAsync(client,
            $"/api/order-operations/incorporations/{confirmation.FirstIncorporation.Id}/contents/1/deliver",
            new { quantity = 1 }, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.OK, delivery.StatusCode);
        using var changed = await PostAsync(client, path, new OrderContextChangeRequest(a, b), Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var liquidation = await PostAsync(client,
            $"/api/order-operations/orders/{orderId}/liquidate-simple",
            new { declaredPaymentMedium = "cash" }, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.OK, liquidation.StatusCode);
        using var frozen = await PostAsync(client, path, new OrderContextChangeRequest(b, a), Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Conflict, frozen.StatusCode);
        Assert.Equal("order.context_change.order_frozen", await ReadCodeAsync(frozen, token));
        using var closeRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{orderId}/close");
        closeRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var closed = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, closeRequest, token);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        using var afterClosure = await PostAsync(client, path, new OrderContextChangeRequest(b, a), Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Conflict, afterClosure.StatusCode);
        Assert.Equal("order.context_change.order_closed", await ReadCodeAsync(afterClosure, token));
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Equal(1, await db.OrderContextChangeHistory.CountAsync(x => x.OrderId == orderId, token));
    }

    [Fact]
    public async Task Concurrent_changes_and_both_liquidation_lock_orders_are_serialized()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var a = await fixture.EnsureConfiguredTestContextAsync("Race A", token);
        var b = await fixture.EnsureConfiguredTestContextAsync("Race B", token);
        var c = await fixture.EnsureConfiguredTestContextAsync("Race C", token);
        var product = await fixture.CreateProductAsync("Race product", "6", token);
        using var actorOne = await fixture.LoginWithoutLegacyRequestAdapterAsync(fixture.DefaultOrderOperationsActor, token);
        var actorTwo = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var actorTwoClient = await fixture.LoginWithoutLegacyRequestAdapterAsync(actorTwo, token);
        using var confirmationRequest = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
        { Content = JsonContent.Create(new FirstConfirmationRequest(a, null, [new FirstConfirmationItemRequest(product.Id, 1)])) };
        confirmationRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var created = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(actorOne, confirmationRequest, token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var confirmed = Assert.IsType<FirstConfirmationResponse>(await created.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
        var orderId = Guid.Parse(confirmed.OperationalReference);
        var contextPath = $"/api/order-operations/orders/{orderId}/context-changes";
        using var gate = new ManualResetEventSlim();
        var firstChange = Task.Run(async () =>
        {
            gate.Wait(token);
            return await PostAsync(actorOne, contextPath, new OrderContextChangeRequest(a, b), Guid.NewGuid(), token);
        }, token);
        var secondChange = Task.Run(async () =>
        {
            gate.Wait(token);
            return await PostAsync(actorTwoClient, contextPath, new OrderContextChangeRequest(a, c), Guid.NewGuid(), token);
        }, token);
        gate.Set();
        using var firstResponse = await firstChange;
        using var secondResponse = await secondChange;
        Assert.Single(new[] { firstResponse.StatusCode, secondResponse.StatusCode }, x => x == HttpStatusCode.OK);
        Assert.Single(new[] { firstResponse, secondResponse }, x => x.StatusCode == HttpStatusCode.Conflict);
        var conflict = firstResponse.StatusCode == HttpStatusCode.Conflict ? firstResponse : secondResponse;
        Assert.Equal("order.context_change.expected_context_stale", await ReadCodeAsync(conflict, token));
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            Assert.Equal(1, await db.OrderContextChangeHistory.CountAsync(x => x.OrderId == orderId, token));
        }

        await AssertOrderedLiquidationRaceAsync(changeFirst: true, token);
        await AssertOrderedLiquidationRaceAsync(changeFirst: false, token);
    }

    [Fact]
    public async Task Freshness_publishes_order_scope_after_commit_and_not_on_replay()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var a = await fixture.EnsureConfiguredTestContextAsync("Freshness A", token);
        var b = await fixture.EnsureConfiguredTestContextAsync("Freshness B", token);
        var product = await fixture.CreateProductAsync("Freshness product", "3", token);
        var destination = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(product.Id, destination, token);
        using var client = await fixture.LoginWithoutLegacyRequestAdapterAsync(fixture.DefaultOrderOperationsActor, token);
        using var confirmationRequest = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
        { Content = JsonContent.Create(new FirstConfirmationRequest(a, null, [new FirstConfirmationItemRequest(product.Id, 1)])) };
        confirmationRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var created = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, confirmationRequest, token);
        var confirmed = Assert.IsType<FirstConfirmationResponse>(await created.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
        var orderId = Guid.Parse(confirmed.OperationalReference);
        var beforeWork = Assert.Single(await fixture.ReadPreparationWorkAsync(token));

        var recorder = new NotificationRecorder(fixture.ConnectionString, orderId);
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(recorder);
        using var notifiedClient = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, token, application, adaptLegacyTestRequests: false);
        var key = Guid.NewGuid();
        var requestBody = new OrderContextChangeRequest(a, b);
        var path = $"/api/order-operations/orders/{orderId}/context-changes";
        using var changed = await PostAsync(notifiedClient, path, requestBody, key, token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var preparationActor = await fixture.CreatePreparationActorAsync(true, destination, token);
        using var preparationClient = await fixture.LoginWithoutLegacyRequestAdapterAsync(preparationActor, token);
        using var preparationRead = await preparationClient.GetAsync(
            $"/api/order-operations/preparation/work?preparationResponsibilityId={destination}", token);
        Assert.Equal(HttpStatusCode.OK, preparationRead.StatusCode);
        var preparationWork = Assert.Single(Assert.IsType<PreparationWorkResponse[]>(
            await preparationRead.Content.ReadFromJsonAsync<PreparationWorkResponse[]>(token)));
        Assert.Equal(beforeWork.Id, preparationWork.WorkId);
        Assert.Equal("Freshness B", preparationWork.Context);
        Assert.Equal(2, recorder.Notifications.Count);
        var notification = Assert.Single(recorder.Notifications, x => x.Kind == "order.changed");
        Assert.Equal("order.changed", notification.Kind);
        Assert.Equal(orderId, notification.Scope.ScopeId);
        var preparationNotification = Assert.Single(recorder.Notifications, x => x.Kind == "preparation.destination.changed");
        Assert.Equal(destination, preparationNotification.Scope.DestinationId);
        Assert.All(recorder.CommittedAtPublish, Assert.True);
        using var replay = await PostAsync(notifiedClient, path, requestBody, key, token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(2, recorder.Notifications.Count);
    }

    [Fact]
    public async Task Context_change_requires_order_authority_antiforgery_and_uuid_v4_key()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var a = await fixture.EnsureConfiguredTestContextAsync("Security A", token);
        var b = await fixture.EnsureConfiguredTestContextAsync("Security B", token);
        var product = await fixture.CreateProductAsync("Security product", "3", token);
        using var orderClient = await fixture.LoginWithoutLegacyRequestAdapterAsync(fixture.DefaultOrderOperationsActor, token);
        using var creation = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
        { Content = JsonContent.Create(new FirstConfirmationRequest(a, null, [new FirstConfirmationItemRequest(product.Id, 1)])) };
        creation.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var created = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(orderClient, creation, token);
        var confirmed = Assert.IsType<FirstConfirmationResponse>(await created.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
        var orderId = Guid.Parse(confirmed.OperationalReference);
        var path = $"/api/order-operations/orders/{orderId}/context-changes";
        var intent = new OrderContextChangeRequest(a, b);

        var admin = await fixture.CreateDeliveryActorAsync(false, false, null, token);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var identities = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
            identities.ResponsibilityAssignments.Add(new ResponsibilityAssignment(admin.IdentityId, FunctionalResponsibility.GeneralConfiguration));
            await identities.SaveChangesAsync(token);
        }
        using var adminClient = await fixture.LoginWithoutLegacyRequestAdapterAsync(admin, token);
        using var adminResult = await PostAsync(adminClient, path, intent, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Forbidden, adminResult.StatusCode);

        var preparationActor = await fixture.CreatePreparationActorAsync(true, null, token);
        using var preparationClient = await fixture.LoginWithoutLegacyRequestAdapterAsync(preparationActor, token);
        using var preparationResult = await PostAsync(preparationClient, path, intent, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Forbidden, preparationResult.StatusCode);

        using var invalidKeyRequest = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(intent) };
        invalidKeyRequest.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString("D"));
        using var invalidKey = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(orderClient, invalidKeyRequest, token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidKey.StatusCode);
        Assert.Equal("order.context_change.idempotency_key_invalid", await ReadCodeAsync(invalidKey, token));

        using var missingAntiforgeryRequest = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = JsonContent.Create(intent) };
        missingAntiforgeryRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var missingAntiforgery = await orderClient.SendAsync(missingAntiforgeryRequest, token);
        Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
        Assert.Equal("order.context_change.antiforgery_invalid", await ReadCodeAsync(missingAntiforgery, token));
    }

    private async Task AssertOrderedLiquidationRaceAsync(bool changeFirst, CancellationToken token)
    {
        await fixture.ResetAsync(token);
        var a = await fixture.EnsureConfiguredTestContextAsync(changeFirst ? "Change first A" : "Liquidate first A", token);
        var b = await fixture.EnsureConfiguredTestContextAsync(changeFirst ? "Change first B" : "Liquidate first B", token);
        var productId = (await fixture.CreateProductAsync(changeFirst ? "Change first product" : "Liquidate first product", "6", token)).Id;
        using var client = await fixture.LoginWithoutLegacyRequestAdapterAsync(fixture.DefaultOrderOperationsActor, token);
        using var confirmationRequest = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
        { Content = JsonContent.Create(new FirstConfirmationRequest(a, null, [new FirstConfirmationItemRequest(productId, 1)])) };
        confirmationRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var created = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, confirmationRequest, token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var confirmed = Assert.IsType<FirstConfirmationResponse>(await created.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
        var orderId = Guid.Parse(confirmed.OperationalReference);
        using var delivered = await PostAsync(client,
            $"/api/order-operations/incorporations/{confirmed.FirstIncorporation.Id}/contents/1/deliver",
            new { quantity = 1 }, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
        var contextPath = $"/api/order-operations/orders/{orderId}/context-changes";
        var liquidationPath = $"/api/order-operations/orders/{orderId}/liquidate-simple";
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var blocker = await connection.BeginTransactionAsync(token);
        await using (var lockOrder = connection.CreateCommand())
        {
            lockOrder.Transaction = blocker;
            lockOrder.CommandText = "SELECT id FROM order_operations.orders WHERE id = @id FOR UPDATE";
            lockOrder.Parameters.AddWithValue("id", orderId);
            await lockOrder.ExecuteScalarAsync(token);
        }
        var first = changeFirst
            ? PostAsync(client, contextPath, new OrderContextChangeRequest(a, b), Guid.NewGuid(), token)
            : PostAsync(client, liquidationPath, new { declaredPaymentMedium = "cash" }, Guid.NewGuid(), token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), token));
        var second = changeFirst
            ? PostAsync(client, liquidationPath, new { declaredPaymentMedium = "cash" }, Guid.NewGuid(), token)
            : PostAsync(client, contextPath, new OrderContextChangeRequest(a, b), Guid.NewGuid(), token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), token));
        await blocker.CommitAsync(token);
        using var firstResponse = await first;
        using var secondResponse = await second;
        var changeResponse = changeFirst ? firstResponse : secondResponse;
        var liquidationResponse = changeFirst ? secondResponse : firstResponse;
        if (changeFirst)
        {
            Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, liquidationResponse.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, changeResponse.StatusCode);
            Assert.Equal("order.context_change.order_frozen", await ReadCodeAsync(changeResponse, token));
        }
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var order = await db.Orders.SingleAsync(x => x.Id == orderId, token);
        Assert.Equal(changeFirst ? b : a, order.CurrentContextId);
        Assert.Equal(changeFirst ? 1 : 0, await db.OrderContextChangeHistory.CountAsync(x => x.OrderId == orderId, token));
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T body, Guid key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, token);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed class NotificationRecorder(string connectionString, Guid orderId) : IChangeNotificationPublisher
    {
        internal List<ChangeNotification> Notifications { get; } = [];
        internal List<bool> CommittedAtPublish { get; } = [];
        public void Publish(ChangeNotification notification)
        {
            Notifications.Add(notification);
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM order_operations.order_context_change_history WHERE order_id = @order_id";
            command.Parameters.AddWithValue("order_id", orderId);
            CommittedAtPublish.Add((long)command.ExecuteScalar()! > 0);
        }
    }
}
