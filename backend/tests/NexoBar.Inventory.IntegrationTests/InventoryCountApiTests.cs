using System.Net;
using System.Net.Http.Json;
using System.Text;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryCountApiTests(InventoryApiFixture fixture)
{
    [Theory]
    [InlineData("1", "1")]
    [InlineData("0", "0")]
    [InlineData("12.375", "12.375")]
    [InlineData("1.123456789012", "1.123456789012")]
    [InlineData("0001.5000", "1.5")]
    public async Task Valid_exact_decimal_is_recorded_and_canonicalized(
        string input,
        string canonical)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "Kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, 3m, 7, token);
        var actor = await OperationActorAsync("valid-decimal", token);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.PostCountAsync(
            item.Id, Guid.NewGuid(), input, token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var count = Assert.IsType<CountObservationResponse>(
            await response.Content.ReadFromJsonAsync<CountObservationResponse>(token));
        Assert.Equal(canonical, count.ObservedQuantity);
        Assert.Equal(7, count.ObservedMovementRevision);
        Assert.Equal("Kg", count.ObservedOperationalUnit);
        Assert.Equal(TimeSpan.Zero, count.ObservedAt.Offset);
        var persisted = Assert.Single(await fixture.ReadCountObservationsAsync(token));
        Assert.Equal(actor.IdentityId, persisted.ActorIdentityId);
        Assert.Equal(decimal.Parse(canonical, System.Globalization.CultureInfo.InvariantCulture), persisted.ObservedQuantity);
        var unchanged = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(3m, unchanged.CurrentRegisteredQuantity);
        Assert.Equal(7, unchanged.MovementRevision);
    }

    [Theory]
    [InlineData("1.1234567890123")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1,5")]
    [InlineData("1e2")]
    [InlineData("+1")]
    [InlineData("10000000000000000")]
    public async Task Invalid_decimal_string_is_rejected(string input)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await OperationActorAsync("bad-decimal", token);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.PostCountAsync(
            item.Id, Guid.NewGuid(), input, token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "inventory.count.observed_quantity_invalid",
            token);
        Assert.Equal((0, 0, 0, 0), await fixture.ReadInventoryEffectCountsAsync(token));
    }

    [Fact]
    public async Task Json_number_and_unknown_properties_are_rejected()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await OperationActorAsync("strict-body", token);
        await fixture.LoginAsync(actor, token);
        var antiforgery = await fixture.GetAntiforgeryTokenAsync(token);

        foreach (var json in new[]
                 {
                     "{\"observedQuantity\":1.5}",
                     "{\"observedQuantity\":\"1.5\",\"revision\":0}"
                 })
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/inventory/items/{item.Id:D}/counts")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            request.Headers.Add("X-NexoBar-CSRF", antiforgery);
            using var response = await fixture.Client.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal((0, 0, 0, 0), await fixture.ReadInventoryEffectCountsAsync(token));
    }

    [Fact]
    public async Task Count_requires_inventory_operation_and_existing_item()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var configurationActor = await fixture.CreateActorAsync(
            "count-config-only",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(configurationActor, token);
        using var forbidden = await fixture.PostCountAsync(
            item.Id, Guid.NewGuid(), "1", token);
        await InventoryTestAssertions.AssertProblemAsync(
            forbidden, HttpStatusCode.Forbidden, "inventory.operation.forbidden", token);

        var operationActor = await OperationActorAsync("missing-item", token);
        await fixture.LoginAsync(operationActor, token);
        using var missing = await fixture.PostCountAsync(
            Guid.NewGuid(), Guid.NewGuid(), "1", token);
        await InventoryTestAssertions.AssertProblemAsync(
            missing, HttpStatusCode.NotFound, "inventory.item.not_found", token);
    }

    [Fact]
    public async Task Count_command_failure_rolls_back_observation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await OperationActorAsync("count-atomicity", token);
        await fixture.LoginAsync(actor, token);
        await fixture.SetCountCommandFailureAsync(true, token);
        try
        {
            using var failed = await fixture.PostCountAsync(
                item.Id, Guid.NewGuid(), "2", token);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal((0, 0, 0, 0), await fixture.ReadInventoryEffectCountsAsync(token));
        }
        finally
        {
            await fixture.SetCountCommandFailureAsync(false, token);
        }
    }

    [Theory]
    [InlineData("counts", "{\"observedQuantity\":\"1\"}")]
    [InlineData("reconcile", "{\"countObservationId\":\"00000000-0000-0000-0000-000000000001\"}")]
    public async Task Malformed_item_uuid_is_bad_request(string action, string json)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await OperationActorAsync("bad-item-id", token);
        await fixture.LoginAsync(actor, token);
        var antiforgery = await fixture.GetAntiforgeryTokenAsync(token);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/inventory/items/not-a-uuid/{action}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-NexoBar-CSRF", antiforgery);

        using var response = await fixture.Client.SendAsync(request, token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "inventory.item.id_invalid",
            token);
    }

    private Task<InventoryActor> OperationActorAsync(
        string suffix,
        CancellationToken cancellationToken) =>
        fixture.CreateActorAsync(
            $"{suffix}-{Guid.NewGuid():N}",
            cancellationToken,
            FunctionalResponsibility.InventoryOperation);
}
