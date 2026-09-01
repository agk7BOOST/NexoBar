using System.Net;
using System.Net.Http.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryCreateIdempotencyTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task Exact_semantic_replay_returns_original_without_duplicate()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await CreateConfigurationActorAsync("replay", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();

        using var first = await fixture.PostItemAsync(key, " Harina ", " Kg ", token);
        using var replay = await fixture.PostItemAsync(key, "harina", "Kg", token);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(
            await first.Content.ReadFromJsonAsync<InventoryItemResponse>(token),
            await replay.Content.ReadFromJsonAsync<InventoryItemResponse>(token));
        Assert.Equal((1, 1), await fixture.CountInventoryAsync(token));
    }

    [Fact]
    public async Task Same_key_with_different_name_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await CreateConfigurationActorAsync("different-name", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();
        using var first = await fixture.PostItemAsync(key, "Harina", "kg", token);
        first.EnsureSuccessStatusCode();

        using var conflict = await fixture.PostItemAsync(key, "Azucar", "kg", token);

        await AssertIdempotencyConflictAsync(conflict, token);
        Assert.Equal((1, 1), await fixture.CountInventoryAsync(token));
    }

    [Fact]
    public async Task Same_key_with_different_unit_conflicts_and_preserves_case()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await CreateConfigurationActorAsync("different-unit", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();
        using var first = await fixture.PostItemAsync(key, "Harina", "Kg", token);
        first.EnsureSuccessStatusCode();

        using var conflict = await fixture.PostItemAsync(key, "Harina", "kg", token);

        await AssertIdempotencyConflictAsync(conflict, token);
    }

    [Fact]
    public async Task Same_key_with_different_actor_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var firstActor = await CreateConfigurationActorAsync("actor-a", token);
        var secondActor = await CreateConfigurationActorAsync("actor-b", token);
        var key = Guid.NewGuid();
        await fixture.LoginAsync(firstActor, token);
        using var first = await fixture.PostItemAsync(key, "Harina", "kg", token);
        first.EnsureSuccessStatusCode();
        await fixture.LoginAsync(secondActor, token);

        using var conflict = await fixture.PostItemAsync(key, "Harina", "kg", token);

        await AssertIdempotencyConflictAsync(conflict, token);
        Assert.Equal((1, 1), await fixture.CountInventoryAsync(token));
    }

    [Fact]
    public async Task Replay_after_capability_revoke_succeeds_but_new_key_is_forbidden()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await CreateConfigurationActorAsync("revoked", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();
        using var first = await fixture.PostItemAsync(key, "Harina", "kg", token);
        var original = await first.Content.ReadFromJsonAsync<InventoryItemResponse>(token);
        await fixture.RevokeResponsibilityAsync(
            actor.IdentityId,
            FunctionalResponsibility.InventoryConfiguration,
            token);

        using var replay = await fixture.PostItemAsync(key, "Harina", "kg", token);
        using var newIntent = await fixture.PostItemAsync(
            Guid.NewGuid(),
            "Azucar",
            "kg",
            token);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(
            original,
            await replay.Content.ReadFromJsonAsync<InventoryItemResponse>(token));
        await InventoryTestAssertions.AssertProblemAsync(
            newIntent,
            HttpStatusCode.Forbidden,
            "inventory.configuration.forbidden",
            token);
        Assert.Equal((1, 1), await fixture.CountInventoryAsync(token));
    }

    [Fact]
    public async Task Replay_with_invalid_session_is_401()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await CreateConfigurationActorAsync("session-invalid", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();
        using var first = await fixture.PostItemAsync(key, "Harina", "kg", token);
        first.EnsureSuccessStatusCode();
        await fixture.RevokeSessionsAsync(actor.IdentityId, token);

        using var replay = await fixture.PostItemAsync(key, "Harina", "kg", token);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal((1, 1), await fixture.CountInventoryAsync(token));
    }

    [Fact]
    public async Task Replay_with_inactive_identity_is_401()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await CreateConfigurationActorAsync("identity-inactive", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();
        using var first = await fixture.PostItemAsync(key, "Harina", "kg", token);
        first.EnsureSuccessStatusCode();
        await fixture.DeactivateIdentityAsync(actor.IdentityId, token);

        using var replay = await fixture.PostItemAsync(key, "Harina", "kg", token);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Command_failure_rolls_back_item_and_allows_retry()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await CreateConfigurationActorAsync("atomicity", token);
        await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();
        await fixture.SetCommandFailureAsync(true, token);
        try
        {
            using var failed = await fixture.PostItemAsync(
                key,
                "Harina",
                "kg",
                token);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal((0, 0), await fixture.CountInventoryAsync(token));
        }
        finally
        {
            await fixture.SetCommandFailureAsync(false, token);
        }

        using var retry = await fixture.PostItemAsync(key, "Harina", "kg", token);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal((1, 1), await fixture.CountInventoryAsync(token));
    }

    private Task<InventoryActor> CreateConfigurationActorAsync(
        string suffix,
        CancellationToken cancellationToken) =>
        fixture.CreateActorAsync(
            suffix,
            cancellationToken,
            FunctionalResponsibility.InventoryConfiguration);

    private static Task AssertIdempotencyConflictAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "inventory.item.idempotency_key_conflict",
            cancellationToken);
}
