using System.Net;
using System.Net.Http.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryMovementHistoryAuthorizationTests(
    InventoryApiFixture fixture)
{
    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Anonymous history", "unit", token);

        using var response = await fixture.GetMovementHistoryAsync(item.Id, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Inactive_identity_is_unauthorized()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Inactive history", "unit", token);
        var actor = await fixture.CreateActorAsync(
            $"inactive-history-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, token);
        await fixture.DeactivateIdentityAsync(actor.IdentityId, token);

        using var response = await fixture.GetMovementHistoryAsync(item.Id, token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.Unauthorized,
            "identities_and_capabilities.invalid_session",
            token);
    }

    [Theory]
    [InlineData((int)FunctionalResponsibility.InventoryConfiguration)]
    [InlineData((int)FunctionalResponsibility.GeneralConfiguration)]
    public async Task Unrelated_responsibility_is_forbidden(
        int responsibilityValue)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Forbidden history", "unit", token);
        var actor = await fixture.CreateActorAsync(
            $"forbidden-history-{Guid.NewGuid():N}",
            token,
            (FunctionalResponsibility)responsibilityValue);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.GetMovementHistoryAsync(item.Id, token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            "inventory.operation.forbidden",
            token);
    }

    [Fact]
    public async Task Authentication_without_responsibility_is_forbidden()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Authenticated history", "unit", token);
        var actor = await fixture.CreateActorAsync(
            $"authenticated-history-{Guid.NewGuid():N}",
            token);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.GetMovementHistoryAsync(item.Id, token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            "inventory.operation.forbidden",
            token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inventory_operation_with_or_without_configuration_can_read(
        bool includeConfiguration)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Authorized history", "unit", token);
        var responsibilities = includeConfiguration
            ? new[]
            {
                FunctionalResponsibility.InventoryOperation,
                FunctionalResponsibility.InventoryConfiguration
            }
            : [FunctionalResponsibility.InventoryOperation];
        var actor = await fixture.CreateActorAsync(
            $"authorized-history-{Guid.NewGuid():N}",
            token,
            responsibilities);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.GetMovementHistoryAsync(item.Id, token);

        response.EnsureSuccessStatusCode();
        var result = await response.Content
            .ReadFromJsonAsync<InventoryMovementHistoryResponse>(token);
        Assert.Equal(item.Id, result!.ItemId);
        Assert.Empty(result.Movements);
    }

    [Fact]
    public async Task Existing_item_without_movements_returns_empty_page()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Empty history", "bottle", token);
        await LoginOperatorAsync(token);

        using var response = await fixture.GetMovementHistoryAsync(item.Id, token);

        response.EnsureSuccessStatusCode();
        var result = await response.Content
            .ReadFromJsonAsync<InventoryMovementHistoryResponse>(token);
        Assert.Equal(item.Id, result!.ItemId);
        Assert.Equal("Empty history", result.OperationalName);
        Assert.Equal("bottle", result.OperationalUnit);
        Assert.Empty(result.Movements);
        Assert.Null(result.NextBeforeRevision);
    }

    [Fact]
    public async Task Unknown_item_is_not_found()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await LoginOperatorAsync(token);

        using var response = await fixture.GetMovementHistoryAsync(
            Guid.CreateVersion7(),
            token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            "inventory.item.not_found",
            token);
    }

    [Theory]
    [InlineData("not-a-uuid", null, "inventory.item.id_invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000", null, "inventory.item.id_invalid")]
    [InlineData(null, "?limit=0", "inventory.movement_history.limit_invalid")]
    [InlineData(null, "?limit=101", "inventory.movement_history.limit_invalid")]
    [InlineData(null, "?limit=-1", "inventory.movement_history.limit_invalid")]
    [InlineData(null, "?limit=1.0", "inventory.movement_history.limit_invalid")]
    [InlineData(null, "?beforeRevision=0", "inventory.movement_history.before_revision_invalid")]
    [InlineData(null, "?beforeRevision=-1", "inventory.movement_history.before_revision_invalid")]
    [InlineData(null, "?beforeRevision=text", "inventory.movement_history.before_revision_invalid")]
    public async Task Malformed_target_or_pagination_is_bad_request(
        string? malformedItemId,
        string? query,
        string expectedCode)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Invalid history query", "unit", token);
        await LoginOperatorAsync(token);

        using var response = await InventoryApiFixture.GetMovementHistoryAsync(
            fixture.Client,
            malformedItemId ?? item.Id.ToString("D"),
            token,
            query);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            expectedCode,
            token);
    }

    private async Task LoginOperatorAsync(CancellationToken token)
    {
        var actor = await fixture.CreateActorAsync(
            $"history-reader-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, token);
    }
}
