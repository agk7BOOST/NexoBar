using System.Net;
using System.Net.Http.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryReconciliationIdempotencyTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task Exact_replay_returns_original_after_later_state_progression()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await ActorAsync("old-result", token);
        await fixture.LoginAsync(actor, token);
        var firstCount = await CountAsync(item.Id, "10", token);
        var firstKey = Guid.NewGuid();
        using var first = await fixture.PostReconcileAsync(
            item.Id, firstKey, firstCount.CountObservationId, token);
        var original = await first.Content
            .ReadFromJsonAsync<ReconcileInventoryCountResponse>(token);
        var secondCount = await CountAsync(item.Id, "12", token);
        using var second = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), secondCount.CountObservationId, token);
        second.EnsureSuccessStatusCode();

        using var replay = await fixture.PostReconcileAsync(
            item.Id, firstKey, firstCount.CountObservationId, token);

        Assert.Equal(
            original,
            await replay.Content.ReadFromJsonAsync<ReconcileInventoryCountResponse>(token));
        Assert.Equal("10", original!.ResultingRegisteredQuantity);
        Assert.Equal(1, original.MovementRevision);
        Assert.Equal(12m, (await fixture.ReadItemAsync(item.Id, token)).CurrentRegisteredQuantity);
        Assert.Equal(2, (await fixture.ReadMovementsAsync(token)).Count);
        Assert.Equal((2, 2, 2, 2), await fixture.ReadInventoryEffectCountsAsync(token));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Same_key_with_different_count_or_item_conflicts(
        bool differentCount,
        bool differentItem)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var firstItem = await fixture.AddItemAsync("Harina", "kg", token);
        var secondItem = await fixture.AddItemAsync("Azucar", "kg", token);
        var actor = await ActorAsync("target-conflict", token);
        await fixture.LoginAsync(actor, token);
        var firstCount = await CountAsync(firstItem.Id, "10", token);
        var otherCount = await CountAsync(
            differentItem ? secondItem.Id : firstItem.Id,
            "11",
            token);
        var key = Guid.NewGuid();
        using var first = await fixture.PostReconcileAsync(
            firstItem.Id, key, firstCount.CountObservationId, token);
        first.EnsureSuccessStatusCode();

        using var conflict = await fixture.PostReconcileAsync(
            differentItem ? secondItem.Id : firstItem.Id,
            key,
            differentCount ? otherCount.CountObservationId : firstCount.CountObservationId,
            token);
        await InventoryTestAssertions.AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            "inventory.reconciliation.idempotency_key_conflict",
            token);
    }

    [Fact]
    public async Task Same_key_with_different_actor_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var firstActor = await ActorAsync("actor-a", token);
        var secondActor = await ActorAsync("actor-b", token);
        await fixture.LoginAsync(firstActor, token);
        var count = await CountAsync(item.Id, "1", token);
        var key = Guid.NewGuid();
        using var first = await fixture.PostReconcileAsync(
            item.Id, key, count.CountObservationId, token);
        first.EnsureSuccessStatusCode();
        await fixture.LoginAsync(secondActor, token);

        using var conflict = await fixture.PostReconcileAsync(
            item.Id, key, count.CountObservationId, token);
        await InventoryTestAssertions.AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            "inventory.reconciliation.idempotency_key_conflict",
            token);
    }

    [Fact]
    public async Task Replay_after_revoke_succeeds_new_key_is_forbidden_and_invalid_session_is_401()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await ActorAsync("revoked", token);
        await fixture.LoginAsync(actor, token);
        var count = await CountAsync(item.Id, "1", token);
        var key = Guid.NewGuid();
        using var first = await fixture.PostReconcileAsync(
            item.Id, key, count.CountObservationId, token);
        var original = await first.Content
            .ReadFromJsonAsync<ReconcileInventoryCountResponse>(token);
        await fixture.RevokeResponsibilityAsync(
            actor.IdentityId,
            FunctionalResponsibility.InventoryOperation,
            token);

        using var replay = await fixture.PostReconcileAsync(
            item.Id, key, count.CountObservationId, token);
        Assert.Equal(
            original,
            await replay.Content.ReadFromJsonAsync<ReconcileInventoryCountResponse>(token));
        using var forbidden = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), count.CountObservationId, token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await fixture.RevokeSessionsAsync(actor.IdentityId, token);
        using var unauthorized = await fixture.PostReconcileAsync(
            item.Id, key, count.CountObservationId, token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task Same_count_with_new_key_after_movement_is_invalidated()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await ActorAsync("new-key", token);
        await fixture.LoginAsync(actor, token);
        var count = await CountAsync(item.Id, "1", token);
        using var first = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), count.CountObservationId, token);
        first.EnsureSuccessStatusCode();

        using var second = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), count.CountObservationId, token);
        await InventoryTestAssertions.AssertProblemAsync(
            second,
            HttpStatusCode.Conflict,
            "inventory.reconciliation.count_invalidated",
            token);
        Assert.Single(await fixture.ReadMovementsAsync(token));
    }

    private async Task<CountObservationResponse> CountAsync(
        Guid itemId,
        string quantity,
        CancellationToken token)
    {
        using var response = await fixture.PostCountAsync(
            itemId, Guid.NewGuid(), quantity, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CountObservationResponse>(token))!;
    }

    private Task<InventoryActor> ActorAsync(string suffix, CancellationToken token) =>
        fixture.CreateActorAsync(
            $"reconcile-idem-{suffix}-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);
}
