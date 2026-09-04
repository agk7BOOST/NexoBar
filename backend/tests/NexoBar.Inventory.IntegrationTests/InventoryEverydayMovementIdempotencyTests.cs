using System.Net;
using System.Net.Http.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryEverydayMovementIdempotencyTests(
    InventoryApiFixture fixture)
{
    [Fact]
    public async Task Canonical_replay_returns_original_movement_after_later_progress()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync("Replay item", 10, token);
        var actor = await ActorAsync("replay", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();

        using var originalResponse = await fixture.PostEntryAsync(
            item.Id, key, "5.000", token);
        originalResponse.EnsureSuccessStatusCode();
        var original = (await originalResponse.Content
            .ReadFromJsonAsync<InventoryMovementResponse>(token))!;
        using var laterResponse = await fixture.PostManualExitAsync(
            item.Id, Guid.NewGuid(), "3", token);
        laterResponse.EnsureSuccessStatusCode();

        using var replayResponse = await fixture.PostEntryAsync(
            item.Id, key, "5", token);

        replayResponse.EnsureSuccessStatusCode();
        var replay = (await replayResponse.Content
            .ReadFromJsonAsync<InventoryMovementResponse>(token))!;
        Assert.Equal(original, replay);
        Assert.Equal("15", replay.ResultingRegisteredQuantity);
        Assert.Equal(1, replay.MovementRevision);
        var persisted = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(12, persisted.CurrentRegisteredQuantity);
        Assert.Equal(2, persisted.MovementRevision);
        Assert.Equal(2, (await fixture.ReadMovementsAsync(token)).Count);
        Assert.Equal(2, (await fixture.ReadInventoryEffectCountsAsync(token))
            .MovementCommands);
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("item")]
    [InlineData("kind")]
    public async Task Same_key_with_incompatible_intention_conflicts(string difference)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync("Primary item", 10, token);
        var other = await EstablishedItemAsync("Other item", 10, token);
        var actor = await ActorAsync($"conflict-{difference}", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();
        using var original = await fixture.PostEntryAsync(item.Id, key, "2", token);
        original.EnsureSuccessStatusCode();

        using var conflict = difference switch
        {
            "quantity" => await fixture.PostEntryAsync(item.Id, key, "3", token),
            "item" => await fixture.PostEntryAsync(other.Id, key, "2", token),
            "kind" => await fixture.PostWasteAsync(item.Id, key, "2", token),
            _ => throw new InvalidOperationException()
        };

        await InventoryTestAssertions.AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            "inventory.movement.idempotency_key_conflict",
            token);
        Assert.Single(await fixture.ReadMovementsAsync(token));
    }

    [Fact]
    public async Task Same_key_with_different_actor_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync("Actor item", 10, token);
        var actorA = await ActorAsync("actor-a", token);
        var actorB = await ActorAsync("actor-b", token);
        using var clientA = fixture.CreateAnonymousClient();
        using var clientB = fixture.CreateAnonymousClient();
        await fixture.LoginAsync(clientA, actorA, token);
        await fixture.LoginAsync(clientB, actorB, token);
        var key = Guid.NewGuid();

        using var original = await InventoryApiFixture.PostMovementAsync(
            clientA, item.Id, key, "waste", "1", token);
        original.EnsureSuccessStatusCode();
        using var conflict = await InventoryApiFixture.PostMovementAsync(
            clientB, item.Id, key, "waste", "1.0", token);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Single(await fixture.ReadMovementsAsync(token));
    }

    [Fact]
    public async Task Replay_after_capability_revoke_succeeds_but_new_key_is_forbidden()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync("Revocation item", 10, token);
        var actor = await ActorAsync("revocation", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();
        using var original = await fixture.PostWasteAsync(item.Id, key, "2", token);
        original.EnsureSuccessStatusCode();
        var expected = (await original.Content
            .ReadFromJsonAsync<InventoryMovementResponse>(token))!;
        await fixture.RevokeResponsibilityAsync(
            actor.IdentityId,
            FunctionalResponsibility.InventoryOperation,
            token);

        using var replay = await fixture.PostWasteAsync(item.Id, key, "2.00", token);
        replay.EnsureSuccessStatusCode();
        Assert.Equal(
            expected,
            await replay.Content.ReadFromJsonAsync<InventoryMovementResponse>(token));
        using var fresh = await fixture.PostWasteAsync(
            item.Id, Guid.NewGuid(), "1", token);
        Assert.Equal(HttpStatusCode.Forbidden, fresh.StatusCode);
        Assert.Single(await fixture.ReadMovementsAsync(token));
    }

    [Fact]
    public async Task Replay_requires_usable_session_and_active_identity()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync("Authentication item", 10, token);
        var sessionActor = await ActorAsync("session", token);
        var inactiveActor = await ActorAsync("inactive", token);
        using var sessionClient = fixture.CreateAnonymousClient();
        using var inactiveClient = fixture.CreateAnonymousClient();
        await fixture.LoginAsync(sessionClient, sessionActor, token);
        await fixture.LoginAsync(inactiveClient, inactiveActor, token);
        var sessionKey = Guid.NewGuid();
        var inactiveKey = Guid.NewGuid();
        using (var first = await InventoryApiFixture.PostMovementAsync(
                   sessionClient, item.Id, sessionKey, "entries", "1", token))
        {
            first.EnsureSuccessStatusCode();
        }
        using (var second = await InventoryApiFixture.PostMovementAsync(
                   inactiveClient, item.Id, inactiveKey, "entries", "1", token))
        {
            second.EnsureSuccessStatusCode();
        }

        await fixture.RevokeSessionsAsync(sessionActor.IdentityId, token);
        await fixture.DeactivateIdentityAsync(inactiveActor.IdentityId, token);
        using var invalidSession = await InventoryApiFixture.PostMovementAsync(
            sessionClient, item.Id, sessionKey, "entries", "1", token);
        using var inactiveIdentity = await InventoryApiFixture.PostMovementAsync(
            inactiveClient, item.Id, inactiveKey, "entries", "1", token);

        Assert.Equal(HttpStatusCode.Unauthorized, invalidSession.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, inactiveIdentity.StatusCode);
        Assert.Equal(2, (await fixture.ReadMovementsAsync(token)).Count);
    }

    [Fact]
    public async Task Key_used_by_reconciliation_conflicts_with_quantity_movement()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Cross-kind item", "kg", token);
        var actor = await ActorAsync("reconciliation-kind", token);
        await fixture.LoginAsync(actor, token);
        using var countResponse = await fixture.PostCountAsync(
            item.Id, Guid.NewGuid(), "10", token);
        countResponse.EnsureSuccessStatusCode();
        var count = (await countResponse.Content
            .ReadFromJsonAsync<CountObservationResponse>(token))!;
        var key = Guid.NewGuid();
        using var reconciliation = await fixture.PostReconcileAsync(
            item.Id, key, count.CountObservationId, token);
        reconciliation.EnsureSuccessStatusCode();

        using var conflict = await fixture.PostEntryAsync(item.Id, key, "1", token);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Single(await fixture.ReadMovementsAsync(token));
    }

    private async Task<InventoryItem> EstablishedItemAsync(
        string name,
        decimal quantity,
        CancellationToken token)
    {
        var item = await fixture.AddItemAsync(name, "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, quantity, 0, token);
        return item;
    }

    private Task<InventoryActor> ActorAsync(string suffix, CancellationToken token) =>
        fixture.CreateActorAsync(
            $"idempotency-{suffix}-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);
}
