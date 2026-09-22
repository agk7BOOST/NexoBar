using System.Net;
using System.Net.Http.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryDeleteApiTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task Active_retired_and_awaiting_elements_without_history_can_be_deleted()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await ConfigurationActorAsync("eligible", token);
        await fixture.LoginAsync(actor, token);

        foreach (var (name, retired) in new[]
                 {
                     ("Active delete", false),
                     ("Retired delete", true),
                     ("Awaiting delete", false)
                 })
        {
            var item = await fixture.AddItemAsync(name, "kg", token);
            if (retired)
            {
                using var retire = await fixture.PostRetireAsync(
                    item.Id,
                    Guid.NewGuid(),
                    true,
                    token);
                retire.EnsureSuccessStatusCode();
            }

            using var delete = await fixture.PostDeleteAsync(
                item.Id,
                Guid.NewGuid(),
                token);
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
            var result = await delete.Content.ReadFromJsonAsync<InventoryItemDeleteResponse>(
                token);
            Assert.Equal(new InventoryItemDeleteResponse(item.Id, true), result);
        }

        Assert.Equal(0, (await fixture.CountInventoryAsync(token)).Items);
    }

    [Fact]
    public async Task Counts_and_technical_ledgers_do_not_block_delete_and_observations_are_removed()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await ConfigurationActorAsync("count-ledgers", token);
        await fixture.LoginAsync(actor, token);
        using var create = await fixture.PostItemAsync(
            Guid.NewGuid(),
            "Count ledger delete",
            "kg",
            token);
        var created = await create.Content.ReadFromJsonAsync<InventoryItemResponse>(token);
        var itemId = created!.ItemId;
        await fixture.SetRegisteredStateAsync(itemId, 5, 0, token);

        using var count = await fixture.PostCountAsync(
            itemId,
            Guid.NewGuid(),
            "5",
            token);
        var observation = await count.Content.ReadFromJsonAsync<CountObservationResponse>(token);
        using var reconcile = await fixture.PostReconcileAsync(
            itemId,
            Guid.NewGuid(),
            observation!.CountObservationId,
            token);
        reconcile.EnsureSuccessStatusCode();
        Assert.Equal("no_discrepancy", (await reconcile.Content
            .ReadFromJsonAsync<ReconcileInventoryCountResponse>(token))!.Outcome);

        using var delete = await fixture.PostDeleteAsync(itemId, Guid.NewGuid(), token);
        delete.EnsureSuccessStatusCode();
        Assert.Equal((0, 1, 0, 1), await fixture.ReadInventoryEffectCountsAsync(token));
        Assert.Equal(1, await fixture.CountDeleteCommandsAsync(token));
        Assert.Equal(1, (await fixture.CountInventoryAsync(token)).Commands);

        using var laterCount = await fixture.PostCountAsync(
            itemId,
            Guid.NewGuid(),
            "5",
            token);
        await InventoryTestAssertions.AssertProblemAsync(
            laterCount,
            HttpStatusCode.NotFound,
            "inventory.item.not_found",
            token);
    }

    [Fact]
    public async Task Unused_count_does_not_block_delete()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await ConfigurationActorAsync("unused-count", token);
        await fixture.LoginAsync(actor, token);
        var item = await fixture.AddItemAsync("Unused count delete", "kg", token);
        using var count = await fixture.PostCountAsync(
            item.Id,
            Guid.NewGuid(),
            "2",
            token);
        count.EnsureSuccessStatusCode();

        using var delete = await fixture.PostDeleteAsync(item.Id, Guid.NewGuid(), token);
        delete.EnsureSuccessStatusCode();
        Assert.Empty(await fixture.ReadCountObservationsAsync(token));
    }

    [Fact]
    public async Task Invalidated_count_does_not_block_delete()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await ConfigurationActorAsync("invalidated-count", token);
        await fixture.LoginAsync(actor, token);
        var item = await fixture.AddItemAsync("Invalidated count delete", "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, 5, 0, token);

        using var count = await fixture.PostCountAsync(
            item.Id,
            Guid.NewGuid(),
            "4",
            token);
        count.EnsureSuccessStatusCode();
        using var correction = await fixture.PostUnitCorrectionAsync(
            item.Id,
            Guid.NewGuid(),
            "kg",
            "g",
            token);
        correction.EnsureSuccessStatusCode();
        Assert.NotNull((await fixture.ReadCountObservationsAsync(token)).Single()
            .InvalidatedAtUtc);

        using var delete = await fixture.PostDeleteAsync(item.Id, Guid.NewGuid(), token);
        delete.EnsureSuccessStatusCode();
        Assert.Empty(await fixture.ReadCountObservationsAsync(token));
    }

    [Fact]
    public async Task Movement_history_rejects_delete_and_preserves_item_and_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await ConfigurationActorAsync("history-conflict", token);
        await fixture.LoginAsync(actor, token);
        var item = await fixture.AddItemAsync("History retained", "kg", token);
        using var count = await fixture.PostCountAsync(
            item.Id,
            Guid.NewGuid(),
            "3",
            token);
        var observation = await count.Content.ReadFromJsonAsync<CountObservationResponse>(token);
        using var reconcile = await fixture.PostReconcileAsync(
            item.Id,
            Guid.NewGuid(),
            observation!.CountObservationId,
            token);
        reconcile.EnsureSuccessStatusCode();

        using var delete = await fixture.PostDeleteAsync(item.Id, Guid.NewGuid(), token);
        await InventoryTestAssertions.AssertProblemAsync(
            delete,
            HttpStatusCode.Conflict,
            "inventory.item.delete_movement_history_conflict",
            token);
        Assert.Equal(item.Id, (await fixture.ReadItemAsync(item.Id, token)).Id);
        Assert.Single(await fixture.ReadMovementsAsync(token));
    }

    [Fact]
    public async Task Exact_replay_survives_physical_delete_and_capability_revocation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await ConfigurationActorAsync("replay", token);
        await fixture.LoginAsync(actor, token);
        var item = await fixture.AddItemAsync("Replay delete", "kg", token);
        var key = Guid.NewGuid();

        using var first = await fixture.PostDeleteAsync(item.Id, key, token);
        var firstResult = await first.Content.ReadFromJsonAsync<InventoryItemDeleteResponse>(token);
        first.EnsureSuccessStatusCode();
        Assert.Equal(0, (await fixture.CountInventoryAsync(token)).Items);
        await fixture.RevokeResponsibilityAsync(
            actor.IdentityId,
            FunctionalResponsibility.InventoryConfiguration,
            token);

        using var replay = await fixture.PostDeleteAsync(item.Id, key, token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(
            firstResult,
            await replay.Content.ReadFromJsonAsync<InventoryItemDeleteResponse>(token));
    }

    [Fact]
    public async Task Delete_intent_isolated_by_actor_and_item_and_new_key_is_not_found()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await ConfigurationActorAsync("intent-owner", token);
        var otherActor = await ConfigurationActorAsync("intent-other", token);
        await fixture.LoginAsync(actor, token);
        var item = await fixture.AddItemAsync("Intent owner item", "kg", token);
        var otherItem = await fixture.AddItemAsync("Intent other item", "kg", token);
        var key = Guid.NewGuid();
        using var first = await fixture.PostDeleteAsync(item.Id, key, token);
        first.EnsureSuccessStatusCode();

        using var otherClient = fixture.CreateAnonymousClient();
        await fixture.LoginAsync(otherClient, otherActor, token);
        using var otherActorReplay = await InventoryApiFixture.PostDeleteAsync(
            otherClient,
            item.Id,
            key,
            token);
        await InventoryTestAssertions.AssertProblemAsync(
            otherActorReplay,
            HttpStatusCode.Conflict,
            "inventory.item.delete.idempotency_key_conflict",
            token);

        await fixture.LoginAsync(actor, token);
        using var otherItemReplay = await fixture.PostDeleteAsync(otherItem.Id, key, token);
        await InventoryTestAssertions.AssertProblemAsync(
            otherItemReplay,
            HttpStatusCode.Conflict,
            "inventory.item.delete.idempotency_key_conflict",
            token);

        using var newKey = await fixture.PostDeleteAsync(item.Id, Guid.NewGuid(), token);
        await InventoryTestAssertions.AssertProblemAsync(
            newKey,
            HttpStatusCode.NotFound,
            "inventory.item.not_found",
            token);
    }

    [Fact]
    public async Task Configuration_read_exposes_history_based_delete_hint_and_deleted_items_disappear()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await ConfigurationActorAsync("read-hint", token);
        await fixture.LoginAsync(actor, token);
        var eligible = await fixture.AddItemAsync("Eligible hint", "kg", token);
        var retained = await fixture.AddItemAsync("Retained hint", "kg", token);
        await fixture.SetRegisteredStateAsync(retained.Id, 1, 0, token);
        using var count = await fixture.PostCountAsync(
            retained.Id,
            Guid.NewGuid(),
            "1",
            token);
        var observation = await count.Content.ReadFromJsonAsync<CountObservationResponse>(token);
        using var reconcile = await fixture.PostReconcileAsync(
            retained.Id,
            Guid.NewGuid(),
            observation!.CountObservationId,
            token);
        reconcile.EnsureSuccessStatusCode();

        var before = await fixture.GetConfigurationItemsAsync(token);
        Assert.True(before.Single(item => item.ItemId == eligible.Id).DeleteEligible);
        Assert.True(before.Single(item => item.ItemId == retained.Id).DeleteEligible);

        using var delete = await fixture.PostDeleteAsync(eligible.Id, Guid.NewGuid(), token);
        delete.EnsureSuccessStatusCode();
        Assert.DoesNotContain(
            eligible.Id,
            (await fixture.GetConfigurationItemsAsync(token)).Select(item => item.ItemId));
        Assert.DoesNotContain(
            eligible.Id,
            (await fixture.GetOperationalItemsAsync(token)).Select(item => item.ItemId));
    }

    [Fact]
    public async Task Deleted_name_is_free_for_new_item()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await ConfigurationActorAsync("name-reuse", token);
        await fixture.LoginAsync(actor, token);
        var item = await fixture.AddItemAsync("Reusable delete name", "kg", token);
        using var delete = await fixture.PostDeleteAsync(item.Id, Guid.NewGuid(), token);
        delete.EnsureSuccessStatusCode();

        using var create = await fixture.PostItemAsync(
            Guid.NewGuid(),
            "Reusable delete name",
            "kg",
            token);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    [Fact]
    public async Task Delete_requires_configuration_and_antiforgery()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Delete security", "kg", token);
        var operationActor = await fixture.CreateActorAsync(
            "delete-operation-only",
            token,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(operationActor, token);
        using var forbidden = await fixture.PostDeleteAsync(item.Id, Guid.NewGuid(), token);
        await InventoryTestAssertions.AssertProblemAsync(
            forbidden,
            HttpStatusCode.Forbidden,
            "inventory.configuration.forbidden",
            token);

        var configurationActor = await ConfigurationActorAsync("delete-antiforgery", token);
        await fixture.LoginAsync(configurationActor, token);
        using var missingCsrf = await InventoryApiFixture.PostDeleteAsync(
            fixture.Client,
            item.Id,
            Guid.NewGuid(),
            token,
            antiforgeryToken: "invalid");
        await InventoryTestAssertions.AssertProblemAsync(
            missingCsrf,
            HttpStatusCode.BadRequest,
            "inventory.delete.antiforgery_invalid",
            token);

        var csrf = await fixture.GetAntiforgeryTokenAsync(token);
        using var invalidKey = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/inventory/items/{item.Id:D}/delete");
        invalidKey.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString("D"));
        invalidKey.Headers.Add("X-NexoBar-CSRF", csrf);
        using var invalidKeyResponse = await fixture.Client.SendAsync(invalidKey, token);
        await InventoryTestAssertions.AssertProblemAsync(
            invalidKeyResponse,
            HttpStatusCode.BadRequest,
            "inventory.idempotency_key_invalid",
            token);
    }

    private async Task<InventoryActor> ConfigurationActorAsync(
        string suffix,
        CancellationToken cancellationToken) =>
        await fixture.CreateActorAsync(
            suffix,
            cancellationToken,
            FunctionalResponsibility.InventoryConfiguration,
            FunctionalResponsibility.InventoryOperation);
}
