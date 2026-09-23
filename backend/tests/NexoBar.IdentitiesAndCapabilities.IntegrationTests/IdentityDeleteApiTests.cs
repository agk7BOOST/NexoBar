using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.Inventory;
using NexoBar.OrderOperations;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class IdentityDeleteApiTests(IdentitiesAndCapabilitiesFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Eligible_active_or_inactive_target_is_removed_with_current_state_and_replays(bool active)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var admin = await AdministratorAsync("delete-admin", token);
        var target = await fixture.CreateIdentityAsync("Delete target", active, token);
        await fixture.ProvisionCredentialAsync(target.Id, "delete-target", "secret", token);
        await fixture.InsertAssignmentAsync(target.Id, FunctionalResponsibility.InventoryOperation, token);
        await fixture.InsertEnablementAsync(target.Id, Guid.NewGuid(), token);
        var firstLifecycle = active ? "deactivate" : "activate";
        var secondLifecycle = active ? "activate" : "deactivate";
        using (var first = await SendPostCommandAsync(admin, $"/api/identities/{target.Id:D}/{firstLifecycle}", token))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var second = await SendPostCommandAsync(admin, $"/api/identities/{target.Id:D}/{secondLifecycle}", token))
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var targetClient = fixture.CreateClient();
        if (active) await LoginAsync(targetClient, "delete-target", token);

        var key = Guid.NewGuid();
        using var deleted = await DeleteAsync(admin, target.Id, key, token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var original = await deleted.Content.ReadAsStringAsync(token);
        Assert.DoesNotContain(target.Id, (await fixture.ReadIdentitiesAsync(token)).Select(x => x.Id));
        Assert.Equal(0, await fixture.CountAssignmentsAsync(token) - 1);
        Assert.Equal(0, await fixture.CountEnablementsAsync(token));
        Assert.DoesNotContain(target.Id, (await fixture.ReadSessionsAsync(token)).Select(x => x.IdentityId));
        if (active)
        {
            using var invalid = await targetClient.GetAsync("/api/identity-sessions/current", token);
            Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        }

        using var replay = await DeleteAsync(admin, target.Id, key, token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync(token));
        using var changed = await DeleteAsync(admin, Guid.NewGuid(), key, token);
        Assert.Equal("identities_and_capabilities.idempotency_conflict", await ProblemCodeAsync(changed, token));
        using var missing = await DeleteAsync(admin, target.Id, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task General_configuration_path_and_self_delete_follow_existing_authority()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var first = await fixture.CreateIdentityAsync("First admin", true, token);
        await fixture.ProvisionCredentialAsync(first.Id, "first-delete-admin", "secret", token);
        await fixture.InsertAssignmentAsync(first.Id, FunctionalResponsibility.GeneralConfiguration, token);
        using var firstClient = fixture.CreateClient();
        await LoginAsync(firstClient, "first-delete-admin", token);
        using var lastPath = await DeleteAsync(firstClient, first.Id, Guid.NewGuid(), token);
        Assert.Equal("identities_and_capabilities.last_general_configuration_path", await ProblemCodeAsync(lastPath, token));

        using var secondClient = await AdministratorAsync("second-delete-admin", token);
        using var deleted = await DeleteAsync(firstClient, first.Id, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var invalid = await firstClient.GetAsync("/api/identity-sessions/current", token);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        using var remaining = await secondClient.GetAsync("/api/identities", token);
        Assert.Equal(HttpStatusCode.OK, remaining.StatusCode);
    }

    [Fact]
    public async Task Administrative_command_history_does_not_block_and_other_responsibility_cannot_delete()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var admin = await AdministratorAsync("history-delete-admin", token);
        var target = await fixture.CreateIdentityAsync("Administrative actor", true, token);
        await fixture.ProvisionCredentialAsync(target.Id, "history-target-admin", "secret", token);
        await fixture.InsertAssignmentAsync(target.Id, FunctionalResponsibility.GeneralConfiguration, token);
        using var targetClient = fixture.CreateClient();
        await LoginAsync(targetClient, "history-target-admin", token);
        using (var created = await SendCreateAsync(targetClient, "Administrative child", token))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var other = await fixture.CreateIdentityAsync("Other responsibility", true, token);
        await fixture.ProvisionCredentialAsync(other.Id, "other-delete-actor", "secret", token);
        await fixture.InsertAssignmentAsync(other.Id, FunctionalResponsibility.InventoryConfiguration, token);
        using var otherClient = fixture.CreateClient();
        await LoginAsync(otherClient, "other-delete-actor", token);
        using var denied = await DeleteAsync(otherClient, target.Id, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var deactivated = await SendPostCommandAsync(admin, $"/api/identities/{other.Id:D}/deactivate", token))
            Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        using var inactiveDenied = await DeleteAsync(otherClient, target.Id, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Unauthorized, inactiveDenied.StatusCode);

        var countBefore = await fixture.CountAdministrativeCommandsAsync(token);

        using var deleted = await DeleteAsync(admin, target.Id, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(countBefore + 1, await fixture.CountAdministrativeCommandsAsync(token));
        using var invalid = await targetClient.GetAsync("/api/identities", token);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
    }

    [Fact]
    public async Task Confirmation_and_terminal_order_history_block_delete()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var admin = await AdministratorAsync("order-delete-admin", token);
        var target = await fixture.CreateIdentityAsync("Order actor", false, token);
        var terminalActor = await fixture.CreateIdentityAsync("Terminal actor", false, token);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            var orderId = Guid.CreateVersion7();
            var incorporationId = Guid.CreateVersion7();
            db.Orders.Add(new Order(orderId, Guid.NewGuid(), "Context"));
            db.Incorporations.Add(new Incorporation(incorporationId, orderId, 1));
            db.ConfirmationHistory.Add(new ConfirmationHistory(
                Guid.CreateVersion7(), incorporationId, Guid.NewGuid(), "Context", target.Id, DateTimeOffset.UtcNow));
            db.CompleteCancellationHistory.Add(new CompleteCancellationHistory(
                Guid.CreateVersion7(), orderId, terminalActor.Id, DateTimeOffset.UtcNow, false));
            await db.SaveChangesAsync(token);
        }
        using var rejected = await DeleteAsync(admin, target.Id, Guid.NewGuid(), token);
        Assert.Equal("identities_and_capabilities.functional_history_exists", await ProblemCodeAsync(rejected, token));
        Assert.Contains(target.Id, (await fixture.ReadIdentitiesAsync(token)).Select(x => x.Id));
        using var terminalRejected = await DeleteAsync(admin, terminalActor.Id, Guid.NewGuid(), token);
        Assert.Equal("identities_and_capabilities.functional_history_exists", await ProblemCodeAsync(terminalRejected, token));
    }

    [Fact]
    public async Task Inventory_movement_correction_and_used_count_block_but_unused_count_is_invalidated()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var admin = await AdministratorAsync("inventory-delete-admin", token);
        var item = InventoryItem.TryCreate("Count item", "Kg").Item!;
        var movementActor = await fixture.CreateIdentityAsync("Movement actor", false, token);
        var correctionActor = await fixture.CreateIdentityAsync("Correction actor", false, token);
        var countActor = await fixture.CreateIdentityAsync("Used count actor", false, token);
        var unusedActor = await fixture.CreateIdentityAsync("Unused count actor", false, token);
        var countId = Guid.CreateVersion7();
        var unusedId = Guid.CreateVersion7();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            db.InventoryItems.Add(item);
            db.CountObservations.Add(new CountObservation(countId, item.Id, 0m, 0, "Kg", DateTimeOffset.UtcNow, countActor.Id));
            db.InventoryCountCommands.Add(new InventoryCountCommand(
                Guid.NewGuid(), countActor.Id, item.Id, 0m,
                new CountObservationResponse(countId, item.Id, "0", 0, "Kg", DateTimeOffset.UtcNow)));
            db.CountObservations.Add(new CountObservation(unusedId, item.Id, 1m, 0, "Kg", DateTimeOffset.UtcNow, unusedActor.Id));
            var movementId = Guid.CreateVersion7();
            db.InventoryMovements.Add(InventoryMovement.Entry(movementId, item.Id, 1, 1m, 0m, 1m, DateTimeOffset.UtcNow, movementActor.Id));
            db.InventoryMovementCorrections.Add(new InventoryMovementCorrection(
                Guid.NewGuid(), correctionActor.Id, movementId, item.Id, 1, 2,
                "Entry", 1m, "Entry", 2m, 1m, 2m, DateTimeOffset.UtcNow));
            db.InventoryMovementCommands.Add(new InventoryMovementCommand(
                Guid.NewGuid(), Guid.Empty, item.Id, countId,
                new ReconcileInventoryCountResponse(item.Id, countId, "no_discrepancy",
                    null, null, "0", "0", "0", "0", 0)));
            await db.SaveChangesAsync(token);
        }
        foreach (var blocked in new[] { movementActor.Id, correctionActor.Id, countActor.Id })
        {
            using var rejected = await DeleteAsync(admin, blocked, Guid.NewGuid(), token);
            Assert.Equal("identities_and_capabilities.functional_history_exists", await ProblemCodeAsync(rejected, token));
        }
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await db.CountObservations.Where(x => x.Id == countId).ExecuteDeleteAsync(token);
        }
        using (var stillBlocked = await DeleteAsync(admin, countActor.Id, Guid.NewGuid(), token))
            Assert.Equal("identities_and_capabilities.functional_history_exists", await ProblemCodeAsync(stillBlocked, token));
        using var deleted = await DeleteAsync(admin, unusedActor.Id, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            Assert.NotNull((await db.CountObservations.SingleAsync(x => x.Id == unusedId, token)).InvalidatedAtUtc);
        }
    }

    [Fact]
    public async Task Delete_waits_for_stabilized_functional_actor_and_rejects_committed_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var admin = await AdministratorAsync("race-delete-admin", token);
        var target = await fixture.CreateIdentityAsync("Race actor", true, token);
        await fixture.ProvisionCredentialAsync(target.Id, "race-actor", "secret", token);
        await fixture.InsertAssignmentAsync(target.Id, FunctionalResponsibility.InventoryOperation, token);
        using var actorClient = fixture.CreateClient();
        await LoginAsync(actorClient, "race-actor", token);
        var sessionId = (await fixture.ReadSessionsAsync(token)).Single(x => x.IdentityId == target.Id).Id;
        var item = InventoryItem.TryCreate("Race item", "Kg").Item!;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            db.InventoryItems.Add(item);
            await db.SaveChangesAsync(token);
        }

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        Assert.NotNull(await fixture.StabilizeAsync(target.Id, sessionId, transaction, token));
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var authority = scope.ServiceProvider.GetRequiredService<IInventoryAuthorization>();
            Assert.True(await authority.StabilizeInventoryOperationAsync(target.Id, transaction, token));
        }

        var deleteTask = DeleteAsync(admin, target.Id, Guid.NewGuid(), token);
        await Task.Delay(150, token);
        Assert.False(deleteTask.IsCompleted);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO inventory.inventory_movements
              (id, inventory_item_id, movement_revision, nature, quantity,
               previous_registered_quantity, resulting_registered_quantity,
               count_observation_id, occurred_at, actor_identity_id)
            VALUES (@id, @item, 1, 'Entry', 1, 0, 1, NULL, @at, @actor)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("item", item.Id);
            command.Parameters.AddWithValue("at", DateTimeOffset.UtcNow);
            command.Parameters.AddWithValue("actor", target.Id);
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        using var rejected = await deleteTask.WaitAsync(token);
        Assert.Equal("identities_and_capabilities.functional_history_exists", await ProblemCodeAsync(rejected, token));
        Assert.Contains(target.Id, (await fixture.ReadIdentitiesAsync(token)).Select(x => x.Id));
    }

    [Fact]
    public async Task Delete_and_reconciliation_of_target_count_cannot_both_commit()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var admin = await AdministratorAsync("count-race-delete-admin", token);
        var target = await fixture.CreateIdentityAsync("Pending count actor", false, token);
        var operatorIdentity = await fixture.CreateIdentityAsync("Count reconciler", true, token);
        await fixture.ProvisionCredentialAsync(operatorIdentity.Id, "count-reconciler", "secret", token);
        await fixture.InsertAssignmentAsync(operatorIdentity.Id, FunctionalResponsibility.InventoryOperation, token);
        using var operatorClient = fixture.CreateClient();
        await LoginAsync(operatorClient, "count-reconciler", token);
        var item = InventoryItem.TryCreate("Count race item", "Kg").Item!;
        var countId = Guid.CreateVersion7();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            db.InventoryItems.Add(item);
            db.CountObservations.Add(new CountObservation(
                countId, item.Id, 3m, 0, "Kg", DateTimeOffset.UtcNow, target.Id));
            await db.SaveChangesAsync(token);
        }

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var gate = await connection.BeginTransactionAsync(token);
        await using (var lockCount = new NpgsqlCommand(
            "SELECT id FROM inventory.count_observations WHERE id = @id FOR SHARE", connection, gate))
        {
            lockCount.Parameters.AddWithValue("id", countId);
            Assert.Equal(countId, (Guid)(await lockCount.ExecuteScalarAsync(token))!);
        }

        var deleteTask = DeleteAsync(admin, target.Id, Guid.NewGuid(), token);
        var reconcileTask = ReconcileAsync(operatorClient, item.Id, countId, token);
        await Task.Delay(150, token);
        Assert.False(deleteTask.IsCompleted && reconcileTask.IsCompleted);
        await gate.CommitAsync(token);
        using var deleted = await deleteTask.WaitAsync(token);
        using var reconciled = await reconcileTask.WaitAsync(token);
        Assert.True(
            (deleted.IsSuccessStatusCode && !reconciled.IsSuccessStatusCode) ||
            (!deleted.IsSuccessStatusCode && reconciled.IsSuccessStatusCode));
    }

    private async Task<HttpClient> AdministratorAsync(string login, CancellationToken token)
    {
        var actor = await fixture.CreateIdentityAsync(login, true, token);
        await fixture.ProvisionCredentialAsync(actor.Id, login, "secret", token);
        await fixture.InsertAssignmentAsync(actor.Id, FunctionalResponsibility.GeneralConfiguration, token);
        var client = fixture.CreateClient();
        await LoginAsync(client, login, token);
        return client;
    }

    private static async Task LoginAsync(HttpClient client, string login, CancellationToken token)
    {
        var csrf = await CsrfAsync(client, token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity-sessions")
        {
            Content = JsonContent.Create(new { loginIdentifier = login, secret = "secret" })
        };
        request.Headers.Add("X-NexoBar-CSRF", csrf);
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpResponseMessage> DeleteAsync(
        HttpClient client, Guid id, Guid key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/identities/{id:D}");
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        request.Headers.Add("X-NexoBar-CSRF", await CsrfAsync(client, token));
        return await client.SendAsync(request, token);
    }

    private static async Task<HttpResponseMessage> SendCreateAsync(
        HttpClient client, string name, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/identities")
        {
            Content = JsonContent.Create(new { operationalName = name })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-NexoBar-CSRF", await CsrfAsync(client, token));
        return await client.SendAsync(request, token);
    }

    private static async Task<HttpResponseMessage> SendPostCommandAsync(
        HttpClient client, string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-NexoBar-CSRF", await CsrfAsync(client, token));
        return await client.SendAsync(request, token);
    }

    private static async Task<HttpResponseMessage> ReconcileAsync(
        HttpClient client, Guid itemId, Guid countId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/inventory/items/{itemId:D}/reconcile")
        {
            Content = JsonContent.Create(new { countObservationId = countId })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-NexoBar-CSRF", await CsrfAsync(client, token));
        return await client.SendAsync(request, token);
    }

    private static async Task<string> CsrfAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.GetAsync("/api/security/antiforgery", token);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        return json.RootElement.GetProperty("requestToken").GetString()!;
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response, CancellationToken token)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        return json.RootElement.GetProperty("code").GetString()!;
    }
}
