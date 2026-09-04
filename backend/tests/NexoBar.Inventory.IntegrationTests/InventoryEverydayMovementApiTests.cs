using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Globalization;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryEverydayMovementApiTests(InventoryApiFixture fixture)
{
    [Theory]
    [InlineData("10", "1", "11")]
    [InlineData("10.125", "0.375", "10.5")]
    [InlineData("0", "0.000000000001", "0.000000000001")]
    public async Task Entry_records_exact_history_and_refreshes_operational_state(
        string current,
        string quantity,
        string expected)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync(current, 4, token);
        var actor = await OperationActorAsync("entry", token);
        await fixture.LoginAsync(actor, token);
        var before = DateTimeOffset.UtcNow;

        using var response = await fixture.PostEntryAsync(
            item.Id, Guid.NewGuid(), quantity, token);

        response.EnsureSuccessStatusCode();
        var result = (await response.Content
            .ReadFromJsonAsync<InventoryMovementResponse>(token))!;
        Assert.Equal(item.Id, result.ItemId);
        Assert.Equal(InventoryMovement.EntryNature, result.Nature);
        Assert.Equal(InventoryQuantity.Format(decimal.Parse(quantity, CultureInfo.InvariantCulture)), result.Quantity);
        Assert.Equal(InventoryQuantity.Format(decimal.Parse(current, CultureInfo.InvariantCulture)),
            result.PreviousRegisteredQuantity);
        Assert.Equal(expected, result.ResultingRegisteredQuantity);
        Assert.Equal(5, result.MovementRevision);
        Assert.Equal(TimeSpan.Zero, result.OccurredAt.Offset);
        Assert.InRange(result.OccurredAt, before, DateTimeOffset.UtcNow);

        var persisted = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), persisted.CurrentRegisteredQuantity);
        Assert.Equal(5, persisted.MovementRevision);
        var movement = Assert.Single(await fixture.ReadMovementsAsync(token));
        Assert.Equal(result.MovementId, movement.Id);
        Assert.Equal(InventoryMovement.EntryNature, movement.Nature);
        Assert.Equal(decimal.Parse(quantity, CultureInfo.InvariantCulture), movement.Quantity);
        Assert.Equal(decimal.Parse(current, CultureInfo.InvariantCulture), movement.PreviousRegisteredQuantity);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), movement.ResultingRegisteredQuantity);
        Assert.Null(movement.CountObservationId);
        Assert.Equal(actor.IdentityId, movement.ActorIdentityId);
        Assert.Equal(result.OccurredAt, movement.OccurredAt);

        using var readResponse = await fixture.Client.GetAsync(
            "/api/inventory/operations/items",
            token);
        readResponse.EnsureSuccessStatusCode();
        var operational = Assert.Single((await readResponse.Content
            .ReadFromJsonAsync<InventoryOperationalItemResponse[]>(token))!);
        Assert.Equal(expected, operational.CurrentRegisteredQuantity);
        Assert.True(operational.QuantityEstablished);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture) < 0,
            operational.HasNegativeBalanceInconsistency);
        Assert.Equal(5, operational.AsOfMovementRevision);
    }

    [Theory]
    [InlineData("10", "3", "7")]
    [InlineData("2", "5", "-3")]
    [InlineData("-2", "3", "-5")]
    [InlineData("1.125", "0.375", "0.75")]
    public async Task Manual_exit_subtracts_without_clipping_or_stock_conflict(
        string current,
        string quantity,
        string expected)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync(current, 2, token);
        var actor = await OperationActorAsync("exit", token);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.PostManualExitAsync(
            item.Id, Guid.NewGuid(), quantity, token);

        response.EnsureSuccessStatusCode();
        var result = (await response.Content
            .ReadFromJsonAsync<InventoryMovementResponse>(token))!;
        Assert.Equal(InventoryMovement.ManualExitNature, result.Nature);
        Assert.Equal(expected, result.ResultingRegisteredQuantity);
        var persisted = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), persisted.CurrentRegisteredQuantity);
        Assert.Equal(3, persisted.MovementRevision);
        var movement = Assert.Single(await fixture.ReadMovementsAsync(token));
        Assert.Equal(InventoryMovement.ManualExitNature, movement.Nature);
        Assert.Null(movement.CountObservationId);
    }

    [Theory]
    [InlineData("10", "3", "7")]
    [InlineData("2", "5", "-3")]
    [InlineData("-2.125", "0.375", "-2.5")]
    public async Task Waste_subtracts_exactly_and_preserves_negative_balance(
        string current,
        string quantity,
        string expected)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync(current, 6, token);
        var actor = await OperationActorAsync("waste-math", token);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.PostWasteAsync(
            item.Id, Guid.NewGuid(), quantity, token);

        response.EnsureSuccessStatusCode();
        var result = (await response.Content
            .ReadFromJsonAsync<InventoryMovementResponse>(token))!;
        Assert.Equal(InventoryMovement.WasteNature, result.Nature);
        Assert.Equal(expected, result.ResultingRegisteredQuantity);
        Assert.Equal(7, result.MovementRevision);
        var movement = Assert.Single(await fixture.ReadMovementsAsync(token));
        Assert.Equal(InventoryMovement.WasteNature, movement.Nature);
        Assert.Equal(decimal.Parse(quantity, CultureInfo.InvariantCulture),
            movement.Quantity);
        Assert.Equal(decimal.Parse(current, CultureInfo.InvariantCulture),
            movement.PreviousRegisteredQuantity);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            movement.ResultingRegisteredQuantity);
        Assert.Equal(actor.IdentityId, movement.ActorIdentityId);
        Assert.Null(movement.CountObservationId);
    }

    [Fact]
    public async Task Waste_is_distinct_from_manual_exit_with_the_same_magnitude()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync("10", 0, token);
        var actor = await OperationActorAsync("waste", token);
        await fixture.LoginAsync(actor, token);

        using var exit = await fixture.PostManualExitAsync(
            item.Id, Guid.NewGuid(), "2", token);
        using var waste = await fixture.PostWasteAsync(
            item.Id, Guid.NewGuid(), "2.000", token);

        exit.EnsureSuccessStatusCode();
        waste.EnsureSuccessStatusCode();
        var exitResult = (await exit.Content
            .ReadFromJsonAsync<InventoryMovementResponse>(token))!;
        var wasteResult = (await waste.Content
            .ReadFromJsonAsync<InventoryMovementResponse>(token))!;
        Assert.Equal(InventoryMovement.ManualExitNature, exitResult.Nature);
        Assert.Equal(InventoryMovement.WasteNature, wasteResult.Nature);
        Assert.Equal("8", exitResult.ResultingRegisteredQuantity);
        Assert.Equal("6", wasteResult.ResultingRegisteredQuantity);
        var movements = await fixture.ReadMovementsAsync(token);
        Assert.Equal(
            [InventoryMovement.ManualExitNature, InventoryMovement.WasteNature],
            movements.Select(value => value.Nature));
        Assert.All(movements, movement => Assert.Null(movement.CountObservationId));
    }

    [Theory]
    [InlineData("entries")]
    [InlineData("manual-exits")]
    [InlineData("waste")]
    public async Task Uninitialized_item_rejects_everyday_movement_without_effect(
        string route)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await OperationActorAsync($"uninitialized-{route}", token);
        await fixture.LoginAsync(actor, token);

        using var response = await InventoryApiFixture.PostMovementAsync(
            fixture.Client,
            item.Id,
            Guid.NewGuid(),
            route,
            "1",
            token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "inventory.quantity_not_established",
            token);
        var persisted = await fixture.ReadItemAsync(item.Id, token);
        Assert.Null(persisted.CurrentRegisteredQuantity);
        Assert.Equal(0, persisted.MovementRevision);
        Assert.Equal((0, 0, 0, 0),
            await fixture.ReadInventoryEffectCountsAsync(token));
    }

    [Fact]
    public async Task Count_and_reconciliation_enable_all_three_movements()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await OperationActorAsync("establish", token);
        await fixture.LoginAsync(actor, token);
        using var countResponse = await fixture.PostCountAsync(
            item.Id, Guid.NewGuid(), "10", token);
        countResponse.EnsureSuccessStatusCode();
        var count = (await countResponse.Content
            .ReadFromJsonAsync<CountObservationResponse>(token))!;
        using var reconcileResponse = await fixture.PostReconcileAsync(
            item.Id, Guid.NewGuid(), count.CountObservationId, token);
        reconcileResponse.EnsureSuccessStatusCode();

        using var entry = await fixture.PostEntryAsync(
            item.Id, Guid.NewGuid(), "1", token);
        using var exit = await fixture.PostManualExitAsync(
            item.Id, Guid.NewGuid(), "2", token);
        using var waste = await fixture.PostWasteAsync(
            item.Id, Guid.NewGuid(), "3", token);

        Assert.All([entry, exit, waste], value => value.EnsureSuccessStatusCode());
        var persisted = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(6, persisted.CurrentRegisteredQuantity);
        Assert.Equal(4, persisted.MovementRevision);
        Assert.Equal(4, (await fixture.ReadMovementsAsync(token)).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("-1")]
    [InlineData("1e2")]
    [InlineData("1,2")]
    [InlineData("1.")]
    [InlineData("0.0000000000001")]
    [InlineData("10000000000000000")]
    public async Task Invalid_quantity_contract_is_rejected(string? quantity)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync("10", 1, token);
        var actor = await OperationActorAsync($"invalid-{Guid.NewGuid():N}", token);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.PostEntryAsync(
            item.Id, Guid.NewGuid(), quantity, token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "inventory.movement.quantity_invalid",
            token);
        Assert.Equal((0, 0, 0, 0),
            await fixture.ReadInventoryEffectCountsAsync(token));
    }

    [Fact]
    public async Task Json_number_and_unknown_fields_are_rejected()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync("10", 1, token);
        var actor = await OperationActorAsync("strict-json", token);
        await fixture.LoginAsync(actor, token);
        var csrf = await fixture.GetAntiforgeryTokenAsync(token);

        foreach (var json in new[]
                 {
                     "{\"quantity\":1}",
                     "{\"quantity\":\"1\",\"reason\":\"damage\"}"
                 })
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/inventory/items/{item.Id:D}/waste")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            request.Headers.Add("X-NexoBar-CSRF", csrf);
            using var response = await fixture.Client.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("9999999999999999", "1", "entries")]
    [InlineData("-9999999999999999", "1", "manual-exits")]
    [InlineData("-9999999999999999", "1", "waste")]
    public async Task Valid_input_that_exceeds_result_storage_range_is_conflict(
        string current,
        string quantity,
        string route)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync(current, 3, token);
        var actor = await OperationActorAsync($"range-{route}", token);
        await fixture.LoginAsync(actor, token);

        using var response = await InventoryApiFixture.PostMovementAsync(
            fixture.Client,
            item.Id,
            Guid.NewGuid(),
            route,
            quantity,
            token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "inventory.movement.result_out_of_range",
            token);
        var persisted = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(decimal.Parse(current, CultureInfo.InvariantCulture), persisted.CurrentRegisteredQuantity);
        Assert.Equal(3, persisted.MovementRevision);
        Assert.Empty(await fixture.ReadMovementsAsync(token));
    }

    [Fact]
    public async Task New_intention_requires_session_identity_capability_and_item()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync("10", 1, token);
        using var anonymous = await fixture.PostEntryAsync(
            item.Id, Guid.NewGuid(), "1", token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var configurationActor = await fixture.CreateActorAsync(
            "movement-config-only",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(configurationActor, token);
        using var forbidden = await fixture.PostEntryAsync(
            item.Id, Guid.NewGuid(), "1", token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var operationActor = await OperationActorAsync("missing-item", token);
        await fixture.LoginAsync(operationActor, token);
        using var missing = await fixture.PostWasteAsync(
            Guid.NewGuid(), Guid.NewGuid(), "1", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Configuration_read_after_movement_exposes_no_balance_or_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync("10", 0, token);
        var actor = await fixture.CreateActorAsync(
            "configuration-projection",
            token,
            FunctionalResponsibility.InventoryOperation,
            FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(actor, token);
        using var movement = await fixture.PostEntryAsync(
            item.Id, Guid.NewGuid(), "2", token);
        movement.EnsureSuccessStatusCode();

        using var response = await fixture.Client.GetAsync(
            "/api/inventory/configuration/items",
            token);

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(token));
        var result = Assert.Single(document.RootElement.EnumerateArray());
        Assert.True(result.TryGetProperty("itemId", out _));
        Assert.True(result.TryGetProperty("operationalName", out _));
        Assert.True(result.TryGetProperty("operationalUnit", out _));
        Assert.False(result.TryGetProperty("currentRegisteredQuantity", out _));
        Assert.False(result.TryGetProperty("movementRevision", out _));
        Assert.False(result.TryGetProperty("movements", out _));
    }

    [Fact]
    public async Task Revision_overflow_is_technical_and_has_no_effect()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync("10", long.MaxValue, token);
        var actor = await OperationActorAsync("revision-overflow", token);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.PostEntryAsync(
            item.Id, Guid.NewGuid(), "1", token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "inventory.movement.revision_overflow",
            token);
        var persisted = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(10, persisted.CurrentRegisteredQuantity);
        Assert.Equal(long.MaxValue, persisted.MovementRevision);
        Assert.Empty(await fixture.ReadMovementsAsync(token));
    }

    private async Task<InventoryItem> EstablishedItemAsync(
        string quantity,
        long revision,
        CancellationToken token)
    {
        var item = await fixture.AddItemAsync(
            $"Item {Guid.NewGuid():N}", "kg", token);
        await fixture.SetRegisteredStateAsync(
            item.Id,
            decimal.Parse(quantity, CultureInfo.InvariantCulture),
            revision,
            token);
        return item;
    }

    private Task<InventoryActor> OperationActorAsync(
        string suffix,
        CancellationToken token) =>
        fixture.CreateActorAsync(
            $"movement-{suffix}-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);
}
