using System.Net.Http.Json;
using System.Text.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryMovementHistoryApiTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task Real_movement_flow_is_explained_newest_first()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Flour history", "kg", token);
        var counterName = $"history-counter-{Guid.NewGuid():N}";
        var counter = await OperationActorAsync(counterName, token);
        await fixture.LoginAsync(counter, token);
        var before = DateTimeOffset.UtcNow;
        await CountAndReconcileAsync(item.Id, "10", token);

        var entryName = $"history-entry-{Guid.NewGuid():N}";
        var entryActor = await OperationActorAsync(entryName, token);
        await fixture.LoginAsync(entryActor, token);
        using (var entry = await fixture.PostEntryAsync(
                   item.Id, Guid.NewGuid(), "0.125000000000", token))
        {
            entry.EnsureSuccessStatusCode();
        }

        var exitName = $"history-exit-{Guid.NewGuid():N}";
        var exitActor = await OperationActorAsync(exitName, token);
        await fixture.LoginAsync(exitActor, token);
        using (var exit = await fixture.PostManualExitAsync(
                   item.Id, Guid.NewGuid(), "1.125", token))
        {
            exit.EnsureSuccessStatusCode();
        }

        var wasteName = $"history-waste-{Guid.NewGuid():N}";
        var wasteActor = await OperationActorAsync(wasteName, token);
        await fixture.LoginAsync(wasteActor, token);
        using (var waste = await fixture.PostWasteAsync(
                   item.Id, Guid.NewGuid(), "2", token))
        {
            waste.EnsureSuccessStatusCode();
        }

        await fixture.LoginAsync(counter, token);
        await CountAndReconcileAsync(item.Id, "8", token);

        using var response = await fixture.GetMovementHistoryAsync(item.Id, token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(token);
        var result = JsonSerializer.Deserialize<InventoryMovementHistoryResponse>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(item.Id, result.ItemId);
        Assert.Equal("Flour history", result.OperationalName);
        Assert.Equal("kg", result.OperationalUnit);
        Assert.Null(result.NextBeforeRevision);
        Assert.Equal([5L, 4L, 3L, 2L, 1L],
            result.Movements.Select(movement => movement.MovementRevision));
        Assert.Equal(
            ["reconciliation", "waste", "manual_exit", "entry", "reconciliation"],
            result.Movements.Select(movement => movement.Nature));

        AssertMovement(
            result.Movements[0],
            "8", "1", "7", "8", counter.IdentityId,
            $"Inventory actor {counterName}");
        Assert.Equal("8", result.Movements[0].Reconciliation!.ObservedQuantity);
        Assert.Equal("1", result.Movements[0].Reconciliation!.Difference);
        Assert.False(result.Movements[0].Reconciliation!.EstablishedQuantity);
        AssertMovement(
            result.Movements[1],
            "2", "-2", "9", "7", wasteActor.IdentityId,
            $"Inventory actor {wasteName}");
        AssertMovement(
            result.Movements[2],
            "1.125", "-1.125", "10.125", "9", exitActor.IdentityId,
            $"Inventory actor {exitName}");
        AssertMovement(
            result.Movements[3],
            "0.125", "0.125", "10", "10.125", entryActor.IdentityId,
            $"Inventory actor {entryName}");
        AssertMovement(
            result.Movements[4],
            "10", null, null, "10", counter.IdentityId,
            $"Inventory actor {counterName}");
        Assert.True(result.Movements.All(movement =>
            movement.OccurredAt >= before &&
            movement.OccurredAt <= DateTimeOffset.UtcNow &&
            movement.OccurredAt.Offset == TimeSpan.Zero));
        Assert.All(result.Movements.Where(movement =>
            movement.Nature != "reconciliation"),
            movement => Assert.Null(movement.Reconciliation));

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty(
            "currentRegisteredQuantity", out _));
        Assert.False(document.RootElement.TryGetProperty(
            "quantityEstablished", out _));
        Assert.False(document.RootElement.TryGetProperty(
            "asOfMovementRevision", out _));
        var serializedMovement = document.RootElement
            .GetProperty("movements")[0];
        Assert.False(serializedMovement.TryGetProperty("sessionId", out _));
        Assert.False(serializedMovement.TryGetProperty("idempotencyKey", out _));
        Assert.False(serializedMovement.TryGetProperty("commandKind", out _));
        Assert.False(serializedMovement.TryGetProperty("countObservationId", out _));
        Assert.False(serializedMovement.TryGetProperty("responsibilities", out _));
    }

    [Theory]
    [InlineData("10")]
    [InlineData("0")]
    public async Task Initial_reconciliation_preserves_unestablished_baseline(
        string observed)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync(
            $"Initial history {observed}", "unit", token);
        var actor = await OperationActorAsync(
            $"initial-{observed}-{Guid.NewGuid():N}", token);
        await fixture.LoginAsync(actor, token);
        await CountAndReconcileAsync(item.Id, observed, token);

        var movement = await ReadSingleAsync(item.Id, token);

        Assert.Equal("reconciliation", movement.Nature);
        Assert.Equal(observed, movement.Quantity);
        Assert.Null(movement.PreviousRegisteredQuantity);
        Assert.Null(movement.SignedEffect);
        Assert.Equal(observed, movement.ResultingRegisteredQuantity);
        Assert.Equal(observed, movement.Reconciliation!.ObservedQuantity);
        Assert.Null(movement.Reconciliation.Difference);
        Assert.True(movement.Reconciliation.EstablishedQuantity);
    }

    [Theory]
    [InlineData("7", "-3")]
    [InlineData("12", "2")]
    public async Task Ordinary_reconciliation_exposes_observation_and_difference(
        string observed,
        string difference)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync(
            $"Ordinary history {observed}", "unit", token);
        var actor = await OperationActorAsync(
            $"ordinary-{observed}-{Guid.NewGuid():N}", token);
        await fixture.LoginAsync(actor, token);
        await CountAndReconcileAsync(item.Id, "10", token);
        await CountAndReconcileAsync(item.Id, observed, token);

        using var response = await fixture.GetMovementHistoryAsync(item.Id, token);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content
            .ReadFromJsonAsync<InventoryMovementHistoryResponse>(token))!;
        var movement = result.Movements[0];

        Assert.Equal("reconciliation", movement.Nature);
        Assert.Equal(observed, movement.Quantity);
        Assert.Equal("10", movement.PreviousRegisteredQuantity);
        Assert.Equal(difference, movement.SignedEffect);
        Assert.Equal(observed, movement.ResultingRegisteredQuantity);
        Assert.Equal(observed, movement.Reconciliation!.ObservedQuantity);
        Assert.Equal(difference, movement.Reconciliation.Difference);
        Assert.False(movement.Reconciliation.EstablishedQuantity);
    }

    [Fact]
    public async Task No_discrepancy_count_does_not_fabricate_movement_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("No discrepancy history", "unit", token);
        var actor = await OperationActorAsync(
            $"no-discrepancy-{Guid.NewGuid():N}", token);
        await fixture.LoginAsync(actor, token);
        await CountAndReconcileAsync(item.Id, "10", token);
        var noDiscrepancy = await CountAndReconcileAsync(item.Id, "10.0", token);
        Assert.Equal("no_discrepancy", noDiscrepancy.Outcome);

        using var response = await fixture.GetMovementHistoryAsync(item.Id, token);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content
            .ReadFromJsonAsync<InventoryMovementHistoryResponse>(token))!;

        var movement = Assert.Single(result.Movements);
        Assert.Equal(1, movement.MovementRevision);
        Assert.Equal("reconciliation", movement.Nature);
    }

    [Fact]
    public async Task Equal_manual_exit_and_waste_remain_distinct_natures()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Distinct movement history", "unit", token);
        await fixture.SetRegisteredStateAsync(item.Id, 10m, 0, token);
        var actor = await OperationActorAsync(
            $"distinct-{Guid.NewGuid():N}", token);
        await fixture.LoginAsync(actor, token);
        using (var exit = await fixture.PostManualExitAsync(
                   item.Id, Guid.NewGuid(), "2", token))
        {
            exit.EnsureSuccessStatusCode();
        }
        using (var waste = await fixture.PostWasteAsync(
                   item.Id, Guid.NewGuid(), "2.0", token))
        {
            waste.EnsureSuccessStatusCode();
        }

        using var response = await fixture.GetMovementHistoryAsync(item.Id, token);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content
            .ReadFromJsonAsync<InventoryMovementHistoryResponse>(token))!;

        Assert.Equal(["waste", "manual_exit"],
            result.Movements.Select(movement => movement.Nature));
        Assert.All(result.Movements, movement => Assert.Equal("2", movement.Quantity));
        Assert.All(result.Movements, movement => Assert.Equal("-2", movement.SignedEffect));
    }

    [Fact]
    public async Task Negative_result_is_presented_exactly_without_history_warning()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Negative movement history", "unit", token);
        await fixture.SetRegisteredStateAsync(item.Id, 10m, 0, token);
        var actor = await OperationActorAsync(
            $"negative-{Guid.NewGuid():N}", token);
        await fixture.LoginAsync(actor, token);
        using (var exit = await fixture.PostManualExitAsync(
                   item.Id, Guid.NewGuid(), "12", token))
        {
            exit.EnsureSuccessStatusCode();
        }

        var movement = await ReadSingleAsync(item.Id, token);

        Assert.Equal("10", movement.PreviousRegisteredQuantity);
        Assert.Equal("12", movement.Quantity);
        Assert.Equal("-12", movement.SignedEffect);
        Assert.Equal("-2", movement.ResultingRegisteredQuantity);
    }

    private async Task<ReconcileInventoryCountResponse> CountAndReconcileAsync(
        Guid itemId,
        string observedQuantity,
        CancellationToken token)
    {
        using var countResponse = await fixture.PostCountAsync(
            itemId, Guid.NewGuid(), observedQuantity, token);
        countResponse.EnsureSuccessStatusCode();
        var count = (await countResponse.Content
            .ReadFromJsonAsync<CountObservationResponse>(token))!;
        using var reconcileResponse = await fixture.PostReconcileAsync(
            itemId, Guid.NewGuid(), count.CountObservationId, token);
        reconcileResponse.EnsureSuccessStatusCode();
        return (await reconcileResponse.Content
            .ReadFromJsonAsync<ReconcileInventoryCountResponse>(token))!;
    }

    private async Task<InventoryMovementHistoryEntryResponse> ReadSingleAsync(
        Guid itemId,
        CancellationToken token)
    {
        using var response = await fixture.GetMovementHistoryAsync(itemId, token);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content
            .ReadFromJsonAsync<InventoryMovementHistoryResponse>(token))!;
        return Assert.Single(result.Movements);
    }

    private Task<InventoryActor> OperationActorAsync(
        string suffix,
        CancellationToken token) =>
        fixture.CreateActorAsync(
            suffix,
            token,
            FunctionalResponsibility.InventoryOperation);

    private static void AssertMovement(
        InventoryMovementHistoryEntryResponse movement,
        string quantity,
        string? signedEffect,
        string? previous,
        string resulting,
        Guid actorIdentityId,
        string actorOperationalName)
    {
        Assert.NotEqual(Guid.Empty, movement.MovementId);
        Assert.Equal(quantity, movement.Quantity);
        Assert.Equal(signedEffect, movement.SignedEffect);
        Assert.Equal(previous, movement.PreviousRegisteredQuantity);
        Assert.Equal(resulting, movement.ResultingRegisteredQuantity);
        Assert.Equal(actorIdentityId, movement.ActorIdentityId);
        Assert.Equal(actorOperationalName, movement.ActorOperationalName);
    }
}
