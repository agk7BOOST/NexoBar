using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NexoBar.Host.Notifications;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryOperationInvalidationTests(InventoryApiFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Configuration_creation_publishes_once_without_granting_operation_responsibility()
    {
        await fixture.ResetAsync(Token);
        var actor = await fixture.CreateActorAsync(
            $"configuration-only-{Guid.NewGuid():N}",
            Token,
            FunctionalResponsibility.InventoryConfiguration);
        var notifications = new RecordingPublisher();
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        using var client = application.CreateClient();
        await fixture.LoginAsync(client, actor, Token);
        var key = Guid.NewGuid();

        using (var created = await InventoryApiFixture.PostItemAsync(
            client, key, "Visible item", "kg", Token))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        using (var replay = await InventoryApiFixture.PostItemAsync(
            client, key, "visible item", "kg", Token))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        }

        Assert.Equal(["inventory.operation.changed"], notifications.Kinds);
        notifications.Clear();
        using (var rejected = await InventoryApiFixture.PostItemAsync(
            client, Guid.NewGuid(), " ", "kg", Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        using var conflict = await InventoryApiFixture.PostItemAsync(
            client, key, "Another item", "kg", Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Empty(notifications.Kinds);
    }

    [Fact]
    public async Task Entry_publishes_once_for_a_new_command_but_not_replay()
    {
        var (actor, item, notifications, application, client) =
            await CreateOperationClientAsync(10);
        using (application)
        using (client)
        {
            var key = Guid.NewGuid();
            using (var entry = await InventoryApiFixture.PostMovementAsync(
                client, item.Id, key, "entries", "2", Token))
            {
                entry.EnsureSuccessStatusCode();
            }
            using (var replay = await InventoryApiFixture.PostMovementAsync(
                client, item.Id, key, "entries", "2.0", Token))
            {
                replay.EnsureSuccessStatusCode();
            }

            Assert.Equal(["inventory.operation.changed"], notifications.Kinds);
        }
    }

    [Fact]
    public async Task Manual_exit_publishes_when_the_resulting_quantity_is_negative()
    {
        var (_, item, notifications, application, client) =
            await CreateOperationClientAsync(0);
        using (application)
        using (client)
        {
            using var exit = await InventoryApiFixture.PostMovementAsync(
                client, item.Id, Guid.NewGuid(), "manual-exits", "1", Token);
            exit.EnsureSuccessStatusCode();

            Assert.Equal(-1, (await fixture.ReadItemAsync(item.Id, Token))
                .CurrentRegisteredQuantity);
            Assert.Equal(["inventory.operation.changed"], notifications.Kinds);
        }
    }

    [Fact]
    public async Task Waste_publishes_once()
    {
        var (_, item, notifications, application, client) =
            await CreateOperationClientAsync(4);
        using (application)
        using (client)
        {
            using var waste = await InventoryApiFixture.PostMovementAsync(
                client, item.Id, Guid.NewGuid(), "waste", "1", Token);
            waste.EnsureSuccessStatusCode();

            Assert.Equal(["inventory.operation.changed"], notifications.Kinds);
        }
    }

    [Fact]
    public async Task Count_observation_publishes_nothing()
    {
        var (_, item, notifications, application, client) =
            await CreateOperationClientAsync(4);
        using (application)
        using (client)
        {
            using var count = await InventoryApiFixture.PostCountAsync(
                client, item.Id, Guid.NewGuid(), "5", Token);
            count.EnsureSuccessStatusCode();

            Assert.Empty(notifications.Kinds);
        }
    }

    [Fact]
    public async Task State_changing_reconciliation_including_initial_establishment_publishes_once()
    {
        var (_, item, notifications, application, client) =
            await CreateOperationClientAsync(null);
        using (application)
        using (client)
        {
            using var countResponse = await InventoryApiFixture.PostCountAsync(
                client, item.Id, Guid.NewGuid(), "5", Token);
            var count = Assert.IsType<CountObservationResponse>(
                await countResponse.Content.ReadFromJsonAsync<CountObservationResponse>(Token));
            Assert.Empty(notifications.Kinds);

            using var reconciliation = await InventoryApiFixture.PostReconcileAsync(
                client, item.Id, Guid.NewGuid(), count.CountObservationId, Token);
            reconciliation.EnsureSuccessStatusCode();

            Assert.Equal(["inventory.operation.changed"], notifications.Kinds);
            var persisted = await fixture.ReadItemAsync(item.Id, Token);
            Assert.Equal(5, persisted.CurrentRegisteredQuantity);
            Assert.Equal(1, persisted.MovementRevision);

            notifications.Clear();
            using var laterCountResponse = await InventoryApiFixture.PostCountAsync(
                client, item.Id, Guid.NewGuid(), "7", Token);
            var laterCount = Assert.IsType<CountObservationResponse>(
                await laterCountResponse.Content.ReadFromJsonAsync<CountObservationResponse>(Token));
            using var laterReconciliation = await InventoryApiFixture.PostReconcileAsync(
                client, item.Id, Guid.NewGuid(), laterCount.CountObservationId, Token);
            laterReconciliation.EnsureSuccessStatusCode();
            Assert.Equal(["inventory.operation.changed"], notifications.Kinds);
        }
    }

    [Fact]
    public async Task No_discrepancy_and_stale_reconciliation_publish_nothing()
    {
        var (_, item, notifications, application, client) =
            await CreateOperationClientAsync(5);
        using (application)
        using (client)
        {
            using var equalCountResponse = await InventoryApiFixture.PostCountAsync(
                client, item.Id, Guid.NewGuid(), "5", Token);
            var equalCount = Assert.IsType<CountObservationResponse>(
                await equalCountResponse.Content.ReadFromJsonAsync<CountObservationResponse>(Token));
            using (var noDiscrepancy = await InventoryApiFixture.PostReconcileAsync(
                client, item.Id, Guid.NewGuid(), equalCount.CountObservationId, Token))
            {
                noDiscrepancy.EnsureSuccessStatusCode();
            }
            Assert.Empty(notifications.Kinds);

            using var staleCountResponse = await InventoryApiFixture.PostCountAsync(
                client, item.Id, Guid.NewGuid(), "6", Token);
            var staleCount = Assert.IsType<CountObservationResponse>(
                await staleCountResponse.Content.ReadFromJsonAsync<CountObservationResponse>(Token));
            using (var entry = await InventoryApiFixture.PostMovementAsync(
                client, item.Id, Guid.NewGuid(), "entries", "1", Token))
            {
                entry.EnsureSuccessStatusCode();
            }
            notifications.Clear();
            using var stale = await InventoryApiFixture.PostReconcileAsync(
                client, item.Id, Guid.NewGuid(), staleCount.CountObservationId, Token);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Empty(notifications.Kinds);
        }
    }

    [Fact]
    public async Task Rollback_and_idempotency_conflict_publish_nothing()
    {
        var (_, item, notifications, application, client) =
            await CreateOperationClientAsync(5);
        using (application)
        using (client)
        {
            await fixture.SetMovementFailureAsync(true, Token);
            try
            {
                using var failed = await InventoryApiFixture.PostMovementAsync(
                    client, item.Id, Guid.NewGuid(), "entries", "1", Token);
                Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            }
            finally
            {
                await fixture.SetMovementFailureAsync(false, Token);
            }
            Assert.Empty(notifications.Kinds);

            var key = Guid.NewGuid();
            using (var successful = await InventoryApiFixture.PostMovementAsync(
                client, item.Id, key, "entries", "1", Token))
            {
                successful.EnsureSuccessStatusCode();
            }
            notifications.Clear();
            using var conflict = await InventoryApiFixture.PostMovementAsync(
                client, item.Id, key, "entries", "2", Token);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Empty(notifications.Kinds);
        }
    }

    [Fact]
    public async Task Publisher_failure_after_commit_preserves_success_and_replay()
    {
        await fixture.ResetAsync(Token);
        var item = await fixture.AddItemAsync("Publisher failure", "kg", Token);
        await fixture.SetRegisteredStateAsync(item.Id, 3, 1, Token);
        var actor = await fixture.CreateActorAsync(
            $"publisher-failure-{Guid.NewGuid():N}",
            Token,
            FunctionalResponsibility.InventoryOperation);
        using var application = fixture.CreateApplicationWithChangeNotificationPublisher(
            new ThrowingPublisher());
        using var client = application.CreateClient();
        await fixture.LoginAsync(client, actor, Token);
        var key = Guid.NewGuid();

        using (var entry = await InventoryApiFixture.PostMovementAsync(
            client, item.Id, key, "entries", "2", Token))
        {
            entry.EnsureSuccessStatusCode();
        }
        using (var replay = await InventoryApiFixture.PostMovementAsync(
            client, item.Id, key, "entries", "2.0", Token))
        {
            replay.EnsureSuccessStatusCode();
        }

        var persisted = await fixture.ReadItemAsync(item.Id, Token);
        Assert.Equal(5, persisted.CurrentRegisteredQuantity);
        Assert.Equal(2, persisted.MovementRevision);
        Assert.Single(await fixture.ReadMovementsAsync(Token));
    }

    private async Task<(InventoryActor Actor, InventoryItem Item,
        RecordingPublisher Notifications, WebApplicationFactory<Program> Application,
        HttpClient Client)> CreateOperationClientAsync(decimal? quantity)
    {
        await fixture.ResetAsync(Token);
        var item = await fixture.AddItemAsync($"Operational {Guid.NewGuid():N}", "kg", Token);
        await fixture.SetRegisteredStateAsync(item.Id, quantity, quantity is null ? 0 : 1, Token);
        var actor = await fixture.CreateActorAsync(
            $"operation-{Guid.NewGuid():N}",
            Token,
            FunctionalResponsibility.InventoryOperation);
        var notifications = new RecordingPublisher();
        var application = fixture.CreateApplicationWithChangeNotificationPublisher(notifications);
        var client = application.CreateClient();
        await fixture.LoginAsync(client, actor, Token);
        return (actor, item, notifications, application, client);
    }

    private sealed class RecordingPublisher : IChangeNotificationPublisher
    {
        private readonly List<string> kinds = [];

        internal IReadOnlyList<string> Kinds => kinds;

        public void Publish(ChangeNotification notification)
        {
            lock (kinds)
            {
                kinds.Add(notification.Kind);
            }
        }

        internal void Clear()
        {
            lock (kinds)
            {
                kinds.Clear();
            }
        }
    }

    private sealed class ThrowingPublisher : IChangeNotificationPublisher
    {
        public void Publish(ChangeNotification notification) =>
            throw new InvalidOperationException("Simulated post-commit publisher failure.");
    }
}
