using System.Net;
using System.Net.Http.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryCountIdempotencyTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task Canonical_quantity_replays_original_without_duplicate()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await ActorAsync("canonical", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();
        using var first = await fixture.PostCountAsync(item.Id, key, "1", token);
        var original = await first.Content.ReadFromJsonAsync<CountObservationResponse>(token);
        await fixture.SetRegisteredStateAsync(item.Id, 5m, 4, token);

        using var replay = await fixture.PostCountAsync(item.Id, key, "1.000", token);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(
            original,
            await replay.Content.ReadFromJsonAsync<CountObservationResponse>(token));
        Assert.Equal((1, 1, 0, 0), await fixture.ReadInventoryEffectCountsAsync(token));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Same_key_with_incompatible_quantity_or_item_conflicts(
        bool differentQuantity,
        bool differentItem)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var firstItem = await fixture.AddItemAsync("Harina", "kg", token);
        var secondItem = await fixture.AddItemAsync("Azucar", "kg", token);
        var actor = await ActorAsync("conflict", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();
        using var first = await fixture.PostCountAsync(firstItem.Id, key, "1", token);
        first.EnsureSuccessStatusCode();

        using var conflict = await fixture.PostCountAsync(
            differentItem ? secondItem.Id : firstItem.Id,
            key,
            differentQuantity ? "2" : "1",
            token);

        await InventoryTestAssertions.AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            "inventory.count.idempotency_key_conflict",
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
        var key = Guid.NewGuid();
        await fixture.LoginAsync(firstActor, token);
        using var first = await fixture.PostCountAsync(item.Id, key, "1", token);
        first.EnsureSuccessStatusCode();
        await fixture.LoginAsync(secondActor, token);

        using var conflict = await fixture.PostCountAsync(item.Id, key, "1", token);

        await InventoryTestAssertions.AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            "inventory.count.idempotency_key_conflict",
            token);
    }

    [Fact]
    public async Task Replay_after_revoke_succeeds_new_key_is_forbidden_and_invalid_identity_is_401()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await ActorAsync("revocation", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();
        using var first = await fixture.PostCountAsync(item.Id, key, "1", token);
        var original = await first.Content.ReadFromJsonAsync<CountObservationResponse>(token);
        await fixture.RevokeResponsibilityAsync(
            actor.IdentityId,
            FunctionalResponsibility.InventoryOperation,
            token);

        using var replay = await fixture.PostCountAsync(item.Id, key, "1.0", token);
        Assert.Equal(original, await replay.Content.ReadFromJsonAsync<CountObservationResponse>(token));
        using var forbidden = await fixture.PostCountAsync(
            item.Id, Guid.NewGuid(), "1", token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await fixture.DeactivateIdentityAsync(actor.IdentityId, token);
        using var unauthorized = await fixture.PostCountAsync(item.Id, key, "1", token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    private Task<InventoryActor> ActorAsync(string suffix, CancellationToken token) =>
        fixture.CreateActorAsync(
            $"count-idem-{suffix}-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);
}
