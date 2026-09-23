using System.Net;
using System.Net.Http.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryMovementCorrectionApiTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task Successive_corrections_keep_root_and_apply_delta_from_previous_effective_meaning()
    {
        var ct = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(ct);
        var item = await fixture.AddItemAsync("Correction", "kg", ct);
        await fixture.SetRegisteredStateAsync(item.Id, 0m, 1, ct);
        var actor = await fixture.CreateActorAsync("correction", ct, FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, ct);
        using var entry = await fixture.PostEntryAsync(item.Id, Guid.NewGuid(), "10", ct);
        entry.EnsureSuccessStatusCode();
        var root = (await entry.Content.ReadFromJsonAsync<InventoryMovementResponse>(ct))!;
        var key1 = Guid.NewGuid();
        using var first = await fixture.PostCorrectionAsync(root.MovementId, key1, "Entry", "6", 2, ct);
        first.EnsureSuccessStatusCode();
        var c1 = (await first.Content.ReadFromJsonAsync<InventoryMovementCorrectionResponse>(ct))!;
        Assert.Equal("-4", c1.DeltaApplied);
        Assert.Equal("6", c1.ResultingRegisteredQuantity);
        var key2 = Guid.NewGuid();
        using var second = await fixture.PostCorrectionAsync(root.MovementId, key2, "Waste", "2", 3, ct);
        second.EnsureSuccessStatusCode();
        var c2 = (await second.Content.ReadFromJsonAsync<InventoryMovementCorrectionResponse>(ct))!;
        Assert.Equal("Entry", c2.PreviousNature);
        Assert.Equal("6", c2.PreviousQuantity);
        Assert.Equal("-8", c2.DeltaApplied);
        Assert.Equal("-2", c2.ResultingRegisteredQuantity);

        var itemState = await fixture.ReadItemAsync(item.Id, ct);
        Assert.Equal(-2m, itemState.CurrentRegisteredQuantity);
        using var historyResponse = await fixture.GetMovementHistoryAsync(item.Id, ct);
        historyResponse.EnsureSuccessStatusCode();
        var history = (await historyResponse.Content.ReadFromJsonAsync<InventoryMovementHistoryResponse>(ct))!;
        var rootHistory = Assert.Single(history.Movements);
        Assert.Equal("waste", rootHistory.EffectiveNature);
        Assert.Equal("2", rootHistory.EffectiveQuantity);
        Assert.Equal(2, rootHistory.Corrections!.Count);
        Assert.Equal(root.MovementId, rootHistory.MovementId);

        using var replay = await fixture.PostCorrectionAsync(root.MovementId, key2, "Waste", "2", 3, ct);
        replay.EnsureSuccessStatusCode();
        Assert.True((await replay.Content.ReadFromJsonAsync<InventoryMovementCorrectionResponse>(ct))!.Replayed);
        using var changedIntent = await fixture.PostCorrectionAsync(root.MovementId, key2, "Waste", "3", 3, ct);
        Assert.Equal(HttpStatusCode.Conflict, changedIntent.StatusCode);
        Assert.Equal(4, (await fixture.ReadItemAsync(item.Id, ct)).MovementRevision);
    }

    [Fact]
    public async Task Zero_removes_the_root_effect_and_stale_version_conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(ct);
        var item = await fixture.AddItemAsync("Correction zero", "kg", ct);
        await fixture.SetRegisteredStateAsync(item.Id, 0m, 1, ct);
        var actor = await fixture.CreateActorAsync("correction-zero", ct, FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, ct);
        using var entry = await fixture.PostEntryAsync(item.Id, Guid.NewGuid(), "3", ct);
        entry.EnsureSuccessStatusCode();
        var root = (await entry.Content.ReadFromJsonAsync<InventoryMovementResponse>(ct))!;
        using var correction = await fixture.PostCorrectionAsync(root.MovementId, Guid.NewGuid(), "Entry", "0", 2, ct);
        correction.EnsureSuccessStatusCode();
        Assert.Equal("-3", (await correction.Content.ReadFromJsonAsync<InventoryMovementCorrectionResponse>(ct))!.DeltaApplied);
        using var stale = await fixture.PostCorrectionAsync(root.MovementId, Guid.NewGuid(), "Waste", "1", 2, ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(0m, (await fixture.ReadItemAsync(item.Id, ct)).CurrentRegisteredQuantity);
    }

    [Fact]
    public async Task Later_reconciliation_supersedes_root_and_count_before_correction_is_invalidated()
    {
        var ct = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(ct);
        var item = await fixture.AddItemAsync("Correction reconciliation", "kg", ct);
        await fixture.SetRegisteredStateAsync(item.Id, 0m, 1, ct);
        var actor = await fixture.CreateActorAsync("correction-reconciliation", ct, FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, ct);
        using var entry = await fixture.PostEntryAsync(item.Id, Guid.NewGuid(), "10", ct);
        entry.EnsureSuccessStatusCode();
        var root = (await entry.Content.ReadFromJsonAsync<InventoryMovementResponse>(ct))!;
        using var count = await fixture.PostCountAsync(item.Id, Guid.NewGuid(), "5", ct);
        count.EnsureSuccessStatusCode();
        var observation = (await count.Content.ReadFromJsonAsync<CountObservationResponse>(ct))!;
        using var reconcile = await fixture.PostReconcileAsync(item.Id, Guid.NewGuid(), observation.CountObservationId, ct);
        reconcile.EnsureSuccessStatusCode();
        using var earlierCount = await fixture.PostCountAsync(item.Id, Guid.NewGuid(), "7", ct);
        earlierCount.EnsureSuccessStatusCode();
        var pending = (await earlierCount.Content.ReadFromJsonAsync<CountObservationResponse>(ct))!;
        using var correction = await fixture.PostCorrectionAsync(root.MovementId, Guid.NewGuid(), "Entry", "6", 3, ct);
        correction.EnsureSuccessStatusCode();
        var result = (await correction.Content.ReadFromJsonAsync<InventoryMovementCorrectionResponse>(ct))!;
        Assert.Equal("0", result.DeltaApplied);
        Assert.Equal("5", result.ResultingRegisteredQuantity);
        Assert.Equal(5m, (await fixture.ReadItemAsync(item.Id, ct)).CurrentRegisteredQuantity);

        using var rejectedReconcile = await fixture.PostReconcileAsync(item.Id, Guid.NewGuid(), pending.CountObservationId, ct);
        Assert.Equal(HttpStatusCode.Conflict, rejectedReconcile.StatusCode);
    }

    [Fact]
    public async Task Retired_root_can_be_rectified_without_restoring_balance()
    {
        var ct = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(ct);
        var item = await fixture.AddItemAsync("Correction retired", "kg", ct);
        await fixture.SetRegisteredStateAsync(item.Id, 0m, 1, ct);
        var actor = await fixture.CreateActorAsync("correction-retired", ct, FunctionalResponsibility.InventoryOperation, FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(actor, ct);
        using var entry = await fixture.PostEntryAsync(item.Id, Guid.NewGuid(), "10", ct);
        entry.EnsureSuccessStatusCode();
        var root = (await entry.Content.ReadFromJsonAsync<InventoryMovementResponse>(ct))!;
        using var retire = await fixture.PostRetireAsync(item.Id, Guid.NewGuid(), true, ct);
        retire.EnsureSuccessStatusCode();
        using var correction = await fixture.PostCorrectionAsync(root.MovementId, Guid.NewGuid(), "Waste", "2", 2, ct);
        correction.EnsureSuccessStatusCode();
        var result = (await correction.Content.ReadFromJsonAsync<InventoryMovementCorrectionResponse>(ct))!;
        Assert.Equal("0", result.DeltaApplied);
        Assert.Null(result.ResultingRegisteredQuantity);
        var state = await fixture.ReadItemAsync(item.Id, ct);
        Assert.False(state.IsActive);
        Assert.Null(state.CurrentRegisteredQuantity);
    }

    [Fact]
    public async Task Competing_expected_versions_serialize_with_one_stale_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(ct);
        var item = await fixture.AddItemAsync("Correction race", "kg", ct);
        await fixture.SetRegisteredStateAsync(item.Id, 0m, 1, ct);
        var actor = await fixture.CreateActorAsync("correction-race", ct, FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, ct);
        using var entry = await fixture.PostEntryAsync(item.Id, Guid.NewGuid(), "10", ct);
        entry.EnsureSuccessStatusCode();
        var root = (await entry.Content.ReadFromJsonAsync<InventoryMovementResponse>(ct))!;
        var outcomes = await Task.WhenAll(
            fixture.PostCorrectionAsync(root.MovementId, Guid.NewGuid(), "Entry", "6", 2, ct),
            fixture.PostCorrectionAsync(root.MovementId, Guid.NewGuid(), "Waste", "2", 2, ct));
        try
        {
            Assert.Single(outcomes, response => response.IsSuccessStatusCode);
            Assert.Single(outcomes, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var response in outcomes) response.Dispose();
        }
    }

    [Fact]
    public async Task Correction_requires_inventory_operation_and_only_an_ordinary_root()
    {
        var ct = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(ct);
        var item = await fixture.AddItemAsync("Correction authority", "kg", ct);
        await fixture.SetRegisteredStateAsync(item.Id, 0m, 1, ct);
        var actor = await fixture.CreateActorAsync("correction-authority", ct, FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, ct);
        using var entry = await fixture.PostEntryAsync(item.Id, Guid.NewGuid(), "10", ct);
        entry.EnsureSuccessStatusCode();
        var root = (await entry.Content.ReadFromJsonAsync<InventoryMovementResponse>(ct))!;
        using var invalid = await fixture.PostCorrectionAsync(Guid.NewGuid(), Guid.NewGuid(), "Entry", "2", 2, ct);
        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
        var unauthorizedActor = await fixture.CreateActorAsync("correction-no-authority", ct);
        await fixture.LoginAsync(unauthorizedActor, ct);
        using var forbidden = await fixture.PostCorrectionAsync(root.MovementId, Guid.NewGuid(), "Entry", "6", 2, ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
