using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryReconciliationApiTests(InventoryApiFixture fixture)
{
    [Theory]
    [InlineData("10")]
    [InlineData("0")]
    public async Task Initial_reconciliation_establishes_null_without_fictitious_difference(
        string observedQuantity)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var counter = await ActorAsync("counter", token);
        var reconciler = await ActorAsync("reconciler", token);
        await fixture.LoginAsync(counter, token);
        var count = await RecordAsync(item.Id, observedQuantity, token);
        await fixture.LoginAsync(reconciler, token);

        using var response = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), count.CountObservationId, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = Assert.IsType<ReconcileInventoryCountResponse>(
            await response.Content.ReadFromJsonAsync<ReconcileInventoryCountResponse>(token));
        Assert.Equal("reconciled", result.Outcome);
        Assert.Null(result.PreviousRegisteredQuantity);
        Assert.Null(result.Difference);
        Assert.Equal(observedQuantity, result.ObservedQuantity);
        Assert.Equal(observedQuantity, result.ResultingRegisteredQuantity);
        Assert.Equal(1, result.MovementRevision);
        Assert.NotNull(result.MovementId);
        Assert.NotNull(result.OccurredAt);
        Assert.Equal(TimeSpan.Zero, result.OccurredAt!.Value.Offset);

        var persistedItem = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(decimal.Parse(observedQuantity), persistedItem.CurrentRegisteredQuantity);
        Assert.Equal(1, persistedItem.MovementRevision);
        var movement = Assert.Single(await fixture.ReadMovementsAsync(token));
        Assert.Equal("Reconciliation", movement.Nature);
        Assert.Equal(decimal.Parse(observedQuantity), movement.Quantity);
        Assert.Null(movement.PreviousRegisteredQuantity);
        Assert.Equal(decimal.Parse(observedQuantity), movement.ResultingRegisteredQuantity);
        Assert.Equal(count.CountObservationId, movement.CountObservationId);
        Assert.Equal(reconciler.IdentityId, movement.ActorIdentityId);
        Assert.Equal(1, movement.MovementRevision);
        Assert.Equal((1, 1, 1, 1), await fixture.ReadInventoryEffectCountsAsync(token));

        if (observedQuantity == "0")
        {
            using var read = await fixture.Client.GetAsync(
                "/api/inventory/operations/items",
                token);
            read.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await read.Content.ReadAsStreamAsync(token));
            var returned = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("0", returned.GetProperty("currentRegisteredQuantity").GetString());
            Assert.True(returned.GetProperty("quantityEstablished").GetBoolean());
            Assert.Equal(1, returned.GetProperty("asOfMovementRevision").GetInt64());
        }
    }

    [Theory]
    [InlineData("7", "-3")]
    [InlineData("12", "2")]
    public async Task Ordinary_reconciliation_derives_difference_and_advances_revision(
        string observed,
        string difference)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, 10m, 1, token);
        var actor = await ActorAsync("ordinary", token);
        await fixture.LoginAsync(actor, token);
        var count = await RecordAsync(item.Id, observed, token);

        using var response = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), count.CountObservationId, token);
        var result = Assert.IsType<ReconcileInventoryCountResponse>(
            await response.Content.ReadFromJsonAsync<ReconcileInventoryCountResponse>(token));

        Assert.Equal("reconciled", result.Outcome);
        Assert.Equal("10", result.PreviousRegisteredQuantity);
        Assert.Equal(difference, result.Difference);
        Assert.Equal(observed, result.ResultingRegisteredQuantity);
        Assert.Equal(2, result.MovementRevision);
        var movement = Assert.Single(await fixture.ReadMovementsAsync(token));
        Assert.Equal(10m, movement.PreviousRegisteredQuantity);
        Assert.Equal(decimal.Parse(observed), movement.ResultingRegisteredQuantity);
        Assert.Equal(2, movement.MovementRevision);
        var current = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(decimal.Parse(observed), current.CurrentRegisteredQuantity);
        Assert.Equal(2, current.MovementRevision);
    }

    [Fact]
    public async Task No_discrepancy_persists_only_command_and_does_not_advance_revision()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, 10m, 1, token);
        var actor = await ActorAsync("no-discrepancy", token);
        await fixture.LoginAsync(actor, token);
        var count = await RecordAsync(item.Id, "10.0", token);
        var key = Guid.NewGuid();

        using var response = await fixture.PostReconcileAsync(
            item.Id, key, count.CountObservationId, token);
        var result = Assert.IsType<ReconcileInventoryCountResponse>(
            await response.Content.ReadFromJsonAsync<ReconcileInventoryCountResponse>(token));
        Assert.Equal("no_discrepancy", result.Outcome);
        Assert.Null(result.MovementId);
        Assert.Null(result.OccurredAt);
        Assert.Equal("10", result.PreviousRegisteredQuantity);
        Assert.Equal("0", result.Difference);
        Assert.Equal("10", result.ResultingRegisteredQuantity);
        Assert.Equal(1, result.MovementRevision);
        Assert.Empty(await fixture.ReadMovementsAsync(token));
        Assert.Equal((1, 1, 0, 1), await fixture.ReadInventoryEffectCountsAsync(token));

        using var replay = await fixture.PostReconcileAsync(
            item.Id, key, count.CountObservationId, token);
        Assert.Equal(
            result,
            await replay.Content.ReadFromJsonAsync<ReconcileInventoryCountResponse>(token));
        using var newEvaluation = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), count.CountObservationId, token);
        Assert.Equal("no_discrepancy", (await newEvaluation.Content
            .ReadFromJsonAsync<ReconcileInventoryCountResponse>(token))!.Outcome);
        Assert.Equal((1, 1, 0, 2), await fixture.ReadInventoryEffectCountsAsync(token));
    }

    [Fact]
    public async Task No_discrepancy_does_not_invalidate_another_count_at_same_revision()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, 10m, 1, token);
        var actor = await ActorAsync("no-discrepancy-other", token);
        await fixture.LoginAsync(actor, token);
        var equal = await RecordAsync(item.Id, "10", token);
        var discrepancy = await RecordAsync(item.Id, "8", token);

        using var noChange = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), equal.CountObservationId, token);
        Assert.Equal("no_discrepancy", (await noChange.Content
            .ReadFromJsonAsync<ReconcileInventoryCountResponse>(token))!.Outcome);
        using var changed = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), discrepancy.CountObservationId, token);
        var changedResult = await changed.Content
            .ReadFromJsonAsync<ReconcileInventoryCountResponse>(token);
        Assert.Equal("reconciled", changedResult!.Outcome);
        Assert.Equal("8", changedResult.ResultingRegisteredQuantity);
        Assert.Equal(2, changedResult.MovementRevision);
        Assert.Single(await fixture.ReadMovementsAsync(token));
    }

    [Fact]
    public async Task Movement_from_one_count_invalidates_other_counts_from_same_revision()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, 10m, 1, token);
        var actor = await ActorAsync("multiple-counts", token);
        await fixture.LoginAsync(actor, token);
        var countA = await RecordAsync(item.Id, "8", token);
        var countB = await RecordAsync(item.Id, "9", token);

        using var reconcileB = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), countB.CountObservationId, token);
        reconcileB.EnsureSuccessStatusCode();
        using var reconcileA = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), countA.CountObservationId, token);

        await InventoryTestAssertions.AssertProblemAsync(
            reconcileA,
            HttpStatusCode.Conflict,
            "inventory.reconciliation.count_invalidated",
            token);
        Assert.Equal(9m, (await fixture.ReadItemAsync(item.Id, token)).CurrentRegisteredQuantity);
        Assert.Single(await fixture.ReadMovementsAsync(token));
    }

    [Fact]
    public async Task Unit_snapshot_change_invalidates_observation_without_conversion()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await ActorAsync("unit-change", token);
        await fixture.LoginAsync(actor, token);
        var count = await RecordAsync(item.Id, "5", token);
        await fixture.SetOperationalUnitAsync(item.Id, "bolsa", token);

        using var response = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), count.CountObservationId, token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "inventory.reconciliation.observation_invalidated",
            token);
        Assert.Null((await fixture.ReadItemAsync(item.Id, token)).CurrentRegisteredQuantity);
        Assert.Empty(await fixture.ReadMovementsAsync(token));
    }

    [Fact]
    public async Task Observation_for_another_item_is_indistinguishable_not_found()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var first = await fixture.AddItemAsync("Harina", "kg", token);
        var second = await fixture.AddItemAsync("Azucar", "kg", token);
        var actor = await ActorAsync("wrong-item", token);
        await fixture.LoginAsync(actor, token);
        var count = await RecordAsync(first.Id, "1", token);

        using var response = await fixture.PostReconcileAsync(
            second.Id, Guid.NewGuid(), count.CountObservationId, token);
        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            "inventory.count_observation.not_found",
            token);
    }

    [Fact]
    public async Task Derived_difference_can_exceed_quantity_precision_without_duplication()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        await fixture.SetRegisteredStateAsync(
            item.Id,
            -9_999_999_999_999_999.999999999999m,
            1,
            token);
        var actor = await ActorAsync("wide-difference", token);
        await fixture.LoginAsync(actor, token);
        var count = await RecordAsync(
            item.Id,
            "9999999999999999.999999999999",
            token);

        using var response = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), count.CountObservationId, token);
        var result = await response.Content
            .ReadFromJsonAsync<ReconcileInventoryCountResponse>(token);

        Assert.Equal("19999999999999999.999999999998", result!.Difference);
        Assert.Equal("9999999999999999.999999999999", result.ResultingRegisteredQuantity);
        Assert.Single(await fixture.ReadMovementsAsync(token));
    }

    [Fact]
    public async Task Revision_overflow_is_technical_error_and_rolls_back()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, 1m, long.MaxValue, token);
        var actor = await ActorAsync("revision-overflow", token);
        await fixture.LoginAsync(actor, token);
        var count = await RecordAsync(item.Id, "2", token);

        using var response = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), count.CountObservationId, token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "inventory.reconciliation.revision_overflow",
            token);
        var unchanged = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(1m, unchanged.CurrentRegisteredQuantity);
        Assert.Equal(long.MaxValue, unchanged.MovementRevision);
        Assert.Empty(await fixture.ReadMovementsAsync(token));
    }

    [Fact]
    public async Task Reconciliation_restablishes_null_quantity_at_next_revision()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, null, 4, token);
        var actor = await ActorAsync("restablishment", token);
        await fixture.LoginAsync(actor, token);
        var count = await RecordAsync(item.Id, "3", token);

        using var response = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), count.CountObservationId, token);
        var result = await response.Content
            .ReadFromJsonAsync<ReconcileInventoryCountResponse>(token);

        Assert.Equal("reconciled", result!.Outcome);
        Assert.Null(result.PreviousRegisteredQuantity);
        Assert.Null(result.Difference);
        Assert.Equal(5, result.MovementRevision);
        Assert.Equal(5, Assert.Single(await fixture.ReadMovementsAsync(token)).MovementRevision);
    }

    [Fact]
    public async Task Configuration_read_does_not_leak_reconciled_state_or_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var operatorActor = await ActorAsync("configuration-leak-operator", token);
        await fixture.LoginAsync(operatorActor, token);
        var count = await RecordAsync(item.Id, "3", token);
        using (var reconcile = await fixture.PostReconcileAsync(
                   item.Id, Guid.NewGuid(), count.CountObservationId, token))
        {
            reconcile.EnsureSuccessStatusCode();
        }

        var configurationActor = await fixture.CreateActorAsync(
            $"configuration-leak-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(configurationActor, token);
        using var response = await fixture.Client.GetAsync(
            "/api/inventory/configuration/items",
            token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(token));
        var returned = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(item.Id, returned.GetProperty("itemId").GetGuid());
        Assert.False(returned.TryGetProperty("currentRegisteredQuantity", out _));
        Assert.False(returned.TryGetProperty("movementRevision", out _));
        Assert.False(returned.TryGetProperty("countObservations", out _));
        Assert.False(returned.TryGetProperty("movements", out _));
    }

    private async Task<CountObservationResponse> RecordAsync(
        Guid itemId,
        string quantity,
        CancellationToken token)
    {
        using var response = await fixture.PostCountAsync(
            itemId, Guid.NewGuid(), quantity, token);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<CountObservationResponse>(
            await response.Content.ReadFromJsonAsync<CountObservationResponse>(token));
    }

    private Task<InventoryActor> ActorAsync(string suffix, CancellationToken token) =>
        fixture.CreateActorAsync(
            $"reconcile-{suffix}-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);
}
