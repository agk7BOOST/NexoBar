using System.Net;
using System.Net.Http.Json;
using System.Text;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryCreateApiTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task Inventory_configuration_creates_uninitialized_item()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "create-success",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.PostItemAsync(
            Guid.NewGuid(),
            "  Harina  ",
            "  Kg  ",
            token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = Assert.IsType<InventoryItemResponse>(
            await response.Content.ReadFromJsonAsync<InventoryItemResponse>(token));
        Assert.Equal("Harina", created.OperationalName);
        Assert.Equal("Kg", created.OperationalUnit);
        Assert.Null(created.CurrentRegisteredQuantity);
        Assert.Equal(0, created.MovementRevision);
        var persisted = await fixture.ReadItemAsync(created.ItemId, token);
        Assert.Null(persisted.CurrentRegisteredQuantity);
        Assert.Equal(0, persisted.MovementRevision);
    }

    [Fact]
    public async Task Quantity_is_not_accepted_by_create_request()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "quantity-rejected",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(actor, token);
        var antiforgery = await fixture.GetAntiforgeryTokenAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/items")
        {
            Content = new StringContent(
                """{"operationalName":"Harina","operationalUnit":"kg","quantity":"1"}""",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-NexoBar-CSRF", antiforgery);

        using var response = await fixture.Client.SendAsync(request, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((0, 0), await fixture.CountInventoryAsync(token));
    }

    [Fact]
    public async Task Unauthenticated_create_is_401()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var client = fixture.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/items")
        {
            Content = JsonContent.Create(new
            {
                operationalName = "Harina",
                operationalUnit = "kg"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));

        using var response = await client.SendAsync(request, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal((0, 0), await fixture.CountInventoryAsync(token));
    }

    [Fact]
    public async Task Inactive_identity_create_is_401()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "inactive",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(actor, token);
        await fixture.DeactivateIdentityAsync(actor.IdentityId, token);

        using var response = await fixture.PostItemAsync(
            Guid.NewGuid(),
            "Harina",
            "kg",
            token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal((0, 0), await fixture.CountInventoryAsync(token));
    }

    [Fact]
    public async Task Missing_inventory_configuration_is_403()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync("unrelated", token);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.PostItemAsync(
            Guid.NewGuid(),
            "Harina",
            "kg",
            token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            "inventory.configuration.forbidden",
            token);
        Assert.Equal((0, 0), await fixture.CountInventoryAsync(token));
    }

    [Fact]
    public async Task Inventory_operation_alone_cannot_create_items()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "operation-only",
            token,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.PostItemAsync(
            Guid.NewGuid(),
            "Harina",
            "kg",
            token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            "inventory.configuration.forbidden",
            token);
    }

    [Theory]
    [InlineData("", "kg", "inventory.item.operational_name_invalid")]
    [InlineData("Harina\nIntegral", "kg", "inventory.item.operational_name_invalid")]
    [InlineData("Harina", "", "inventory.item.operational_unit_invalid")]
    [InlineData("Harina", "k\ng", "inventory.item.operational_unit_invalid")]
    public async Task Invalid_name_or_unit_is_rejected(
        string operationalName,
        string operationalUnit,
        string expectedCode)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            $"invalid-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.PostItemAsync(
            Guid.NewGuid(),
            operationalName,
            operationalUnit,
            token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            expectedCode,
            token);
    }
}
