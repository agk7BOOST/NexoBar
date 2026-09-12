using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Host.Notifications;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PreparationDestinationInvalidationTests(OrderOperationsApiFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Confirmation_publishes_each_created_destination_once_and_not_direct_content()
    {
        await fixture.ResetAsync(Token);
        var first = await fixture.CreateProductAsync("Preparado", "5", Token);
        var second = await fixture.CreateProductAsync("Directo", "5", Token);
        var destination = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(first.Id, destination, Token);
        var notifications = new RecordingPublisher();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, Token, application);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest("Mesa SSE",
            [new FirstConfirmationItemRequest(first.Id, 2), new FirstConfirmationItemRequest(second.Id, 1)]))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal([destination], notifications.DestinationIds);
    }

    [Fact]
    public async Task Preparation_progress_and_corrections_publish_only_after_a_new_commit_not_replay()
    {
        await fixture.ResetAsync(Token);
        var destination = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(destination, Token, quantity: 4);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        var actor = await fixture.CreatePreparationActorAsync(true, destination, Token);
        var notifications = new RecordingPublisher();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(actor, Token, application);
        var startKey = Guid.NewGuid();

        using (var start = await PreparationStartTestSupport.PostAsync(client, work.Id, startKey, 3, Token))
            start.EnsureSuccessStatusCode();
        using (var replay = await PreparationStartTestSupport.PostAsync(client, work.Id, startKey, 3, Token))
            replay.EnsureSuccessStatusCode();
        using (var ready = await PreparationReadyTestSupport.PostAsync(client, work.Id, Guid.NewGuid(), 2, Token))
            ready.EnsureSuccessStatusCode();
        using (var correctStart = await CorrectProgressAsync(client, work.Id, "correct-start", 1))
            correctStart.EnsureSuccessStatusCode();
        using (var correctReady = await CorrectProgressAsync(client, work.Id, "correct-ready", 1))
            correctReady.EnsureSuccessStatusCode();

        Assert.Equal([destination, destination, destination, destination], notifications.DestinationIds);
    }

    [Fact]
    public async Task Rejection_and_rollback_publish_nothing()
    {
        await fixture.ResetAsync(Token);
        var destination = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(destination, Token, quantity: 2);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        var actor = await fixture.CreatePreparationActorAsync(true, destination, Token);
        var notifications = new RecordingPublisher();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(actor, Token, application);

        using (var rejected = await PreparationStartTestSupport.PostAsync(client, work.Id, Guid.NewGuid(), 3, Token))
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        await fixture.SetPreparationCommandFailureAsync(true, Token);
        try
        {
            using var failed = await PreparationStartTestSupport.PostAsync(client, work.Id, Guid.NewGuid(), 1, Token);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }
        finally
        {
            await fixture.SetPreparationCommandFailureAsync(false, Token);
        }

        Assert.Empty(notifications.DestinationIds);
    }

    [Fact]
    public async Task Content_delivery_and_delivery_correction_publish_only_preparation_destinations()
    {
        await fixture.ResetAsync(Token);
        var correctionTarget = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 4, 2, Token);
        var deliveryTarget = correctionTarget;
        var directProduct = await fixture.CreateProductAsync("Directo", "5", Token);
        using var directConfirmationResponse = await PostAsync(fixture.OrderOperationsClient,
            "/api/order-operations/first-confirmations",
            new FirstConfirmationRequest("Mesa directa", [new FirstConfirmationItemRequest(directProduct.Id, 4)]));
        directConfirmationResponse.EnsureSuccessStatusCode();
        var directConfirmation = Assert.IsType<FirstConfirmationResponse>(
            await directConfirmationResponse.Content.ReadFromJsonAsync<FirstConfirmationResponse>(Token));
        var directContent = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token),
            content => content.ProductId == directProduct.Id);
        var direct = new DeliveryTarget(directConfirmation.OperationalReference,
            directContent.IncorporationId, directContent.ContentOrdinal, null);
        var works = await fixture.ReadPreparationWorkAsync(Token);
        var correctionDestination = works.Single(work => work.Id == correctionTarget.WorkId).PreparationResponsibilityId;
        var deliveryDestination = works.Single(work => work.Id == deliveryTarget.WorkId).PreparationResponsibilityId;
        var notifications = new RecordingPublisher();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, Token, application);

        using (var correction = await ContentCorrectionTestSupport.PostAsync(client, correctionTarget, Guid.NewGuid(), 1, Token))
            await ContentCorrectionTestSupport.SuccessAsync(correction, Token);
        using (var cancellation = await ContentCancellationTestSupport.PostAsync(client, correctionTarget, Guid.NewGuid(), 1, Token))
            await ContentCancellationTestSupport.SuccessAsync(cancellation, Token);
        using (var delivery = await DeliveryQuantityTestSupport.PostAsync(client, deliveryTarget.IncorporationId, deliveryTarget.ContentOrdinal, Guid.NewGuid(), 1, Token))
            await DeliveryQuantityTestSupport.ReadSuccessAsync(delivery, Token);
        using (var deliveryCorrection = await DeliveryCorrectionTestSupport.PostAsync(client, deliveryTarget, Guid.NewGuid(), 1, Token))
            await DeliveryCorrectionTestSupport.SuccessAsync(deliveryCorrection, Token);
        using (var directCorrection = await ContentCorrectionTestSupport.PostAsync(client, direct, Guid.NewGuid(), 1, Token))
            await ContentCorrectionTestSupport.SuccessAsync(directCorrection, Token);

        Assert.Equal(
            [correctionDestination, correctionDestination, deliveryDestination, deliveryDestination],
            notifications.DestinationIds);
    }

    [Fact]
    public async Task Intervention_and_complete_cancellation_publish_affected_destinations_deduplicated()
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 3, 0, Token);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        var destination = work.PreparationResponsibilityId;
        await fixture.SetPreparationQuantitiesAsync(work.Id, 1, 2, 0, Token);
        await GrantInterventionAsync();
        var notifications = new RecordingPublisher();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, Token, application);

        using (var intervention = await PostAsync(client,
            $"/api/order-operations/intervention/work/{work.Id:D}/in-preparation",
            new OperationalInterventionRequest(1)))
            intervention.EnsureSuccessStatusCode();
        using (var complete = await PostAsync(client,
            $"/api/orders/{target.OperationalReference}/complete-cancellation", null))
            complete.EnsureSuccessStatusCode();

        Assert.Equal([destination, destination], notifications.DestinationIds);
    }

    [Fact]
    public async Task Complete_cancellation_publishes_each_affected_destination_once()
    {
        await fixture.ResetAsync(Token);
        var destinationA = Guid.CreateVersion7();
        var destinationB = Guid.CreateVersion7();
        var productA = await fixture.CreateProductAsync("Destino A", "5", Token);
        var productB = await fixture.CreateProductAsync("Destino B", "5", Token);
        await fixture.SetProductPreparationAsync(productA.Id, destinationA, Token);
        await fixture.SetProductPreparationAsync(productB.Id, destinationB, Token);
        var notifications = new RecordingPublisher();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, Token, application);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest("Mesa multiple",
                [new FirstConfirmationItemRequest(productA.Id, 1), new FirstConfirmationItemRequest(productB.Id, 1)]))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var confirmationResponse = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);
        confirmationResponse.EnsureSuccessStatusCode();
        var confirmation = Assert.IsType<FirstConfirmationResponse>(
            await confirmationResponse.Content.ReadFromJsonAsync<FirstConfirmationResponse>(Token));
        notifications.Clear();

        using var complete = await PostAsync(client,
            $"/api/orders/{confirmation.OperationalReference}/complete-cancellation", null);
        complete.EnsureSuccessStatusCode();

        Assert.Equal(2, notifications.DestinationIds.Count);
        Assert.Contains(destinationA, notifications.DestinationIds);
        Assert.Contains(destinationB, notifications.DestinationIds);
    }

    [Fact]
    public async Task Publisher_failure_after_commit_does_not_change_successful_command_outcome()
    {
        await fixture.ResetAsync(Token);
        var destination = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(destination, Token, quantity: 2);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        var actor = await fixture.CreatePreparationActorAsync(true, destination, Token);
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(new ThrowingPublisher());
        using var client = await fixture.LoginAsync(actor, Token, application);

        using var response = await PreparationStartTestSupport.PostAsync(client, work.Id, Guid.NewGuid(), 1, Token);
        response.EnsureSuccessStatusCode();
        var persisted = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        Assert.Equal((1, 1, 0), (persisted.PendingQuantity, persisted.InPreparationQuantity, persisted.ReadyQuantity));
    }

    private async Task GrantInterventionAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        dbContext.ResponsibilityAssignments.Add(new ResponsibilityAssignment(
            fixture.DefaultOrderOperationsActor.IdentityId,
            FunctionalResponsibility.OperationalIntervention));
        await dbContext.SaveChangesAsync(Token);
    }

    private static async Task<HttpResponseMessage> CorrectProgressAsync(
        HttpClient client, Guid workId, string command, int quantity) =>
        await PostAsync(client,
            $"/api/order-operations/preparation/work/{workId:D}/{command}",
            new CorrectPreparationProgressRequest(quantity));

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string path, object? payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = payload is null ? null : JsonContent.Create(payload)
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);
    }

    private sealed class RecordingPublisher : IChangeNotificationPublisher
    {
        private readonly List<Guid> destinationIds = [];
        internal IReadOnlyList<Guid> DestinationIds => destinationIds;

        public void Publish(ChangeNotification notification)
        {
            lock (destinationIds)
            {
                destinationIds.Add(notification.Scope.DestinationId);
            }
        }

        internal void Clear()
        {
            lock (destinationIds)
            {
                destinationIds.Clear();
            }
        }
    }

    private sealed class ThrowingPublisher : IChangeNotificationPublisher
    {
        public void Publish(ChangeNotification notification) =>
            throw new InvalidOperationException("Simulated post-commit publisher failure.");
    }
}
