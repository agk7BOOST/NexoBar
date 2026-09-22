using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryReadApiTests(InventoryApiFixture fixture)
{
    private const string ConfigurationRoute = "/api/inventory/configuration/items";
    private const string OperationsRoute = "/api/inventory/operations/items";

    [Fact]
    public async Task Configuration_identity_reads_only_configuration_projection()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "Kg", token);
        var actor = await fixture.CreateActorAsync(
            "configuration-read",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.Client.GetAsync(ConfigurationRoute, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(token));
        var returned = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(item.Id, returned.GetProperty("itemId").GetGuid());
        Assert.Equal("Harina", returned.GetProperty("operationalName").GetString());
        Assert.Equal("Kg", returned.GetProperty("operationalUnit").GetString());
        Assert.False(returned.TryGetProperty("currentRegisteredQuantity", out _));
        Assert.False(returned.TryGetProperty("quantityEstablished", out _));
        Assert.False(returned.TryGetProperty("movementRevision", out _));
        Assert.False(returned.TryGetProperty("history", out _));
    }

    [Fact]
    public async Task Configuration_read_includes_retired_items_and_supports_reload_reactivation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var activeReady = await fixture.AddItemAsync(
            "Active ready",
            "kg",
            token);
        await fixture.SetRegisteredStateAsync(activeReady.Id, 4, 0, token);
        var activeAwaiting = await fixture.AddItemAsync(
            "Active awaiting",
            "kg",
            token);
        var retired = await fixture.AddItemAsync(
            "Retired configuration",
            "kg",
            token);
        var actor = await fixture.CreateActorAsync(
            "configuration-lifecycle-read",
            token,
            FunctionalResponsibility.InventoryConfiguration,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, token);

        using (var retire = await fixture.PostRetireAsync(
                   retired.Id,
                   Guid.NewGuid(),
                   true,
                   token))
        {
            retire.EnsureSuccessStatusCode();
        }

        var afterRetire = await fixture.GetConfigurationItemsAsync(token);
        Assert.Equal(
            [activeReady.Id, activeAwaiting.Id, retired.Id],
            afterRetire.Select(item => item.ItemId).OrderBy(id => id));
        var activeReadyProjection = Assert.Single(
            afterRetire,
            item => item.ItemId == activeReady.Id);
        Assert.True(activeReadyProjection.IsActive);
        Assert.True(activeReadyProjection.OrdinaryOperationReady);
        var activeAwaitingProjection = Assert.Single(
            afterRetire,
            item => item.ItemId == activeAwaiting.Id);
        Assert.True(activeAwaitingProjection.IsActive);
        Assert.False(activeAwaitingProjection.OrdinaryOperationReady);
        var retiredProjection = Assert.Single(
            afterRetire,
            item => item.ItemId == retired.Id);
        Assert.False(retiredProjection.IsActive);
        Assert.False(retiredProjection.OrdinaryOperationReady);
        Assert.True(retiredProjection.DeleteEligible);
        Assert.Equal("kg", retiredProjection.OperationalUnit);
        Assert.Equal(
            [activeReady.Id, activeAwaiting.Id],
            (await fixture.GetOperationalItemsAsync(token))
                .Select(item => item.ItemId)
                .OrderBy(id => id));

        using (var reactivate = await fixture.PostReactivateAsync(
                   retired.Id,
                   Guid.NewGuid(),
                   false,
                   null,
                   token))
        {
            reactivate.EnsureSuccessStatusCode();
            var result = (await reactivate.Content.ReadFromJsonAsync<InventoryLifecycleResponse>(
                token))!;
            Assert.Equal(retired.Id, result.ItemId);
            Assert.True(result.IsActive);
            Assert.False(result.OrdinaryOperationReady);
            Assert.True(result.RequiresReconciliation);
        }

        var afterReactivate = Assert.Single(
            await fixture.GetConfigurationItemsAsync(token),
            item => item.ItemId == retired.Id);
        Assert.True(afterReactivate.IsActive);
        Assert.False(afterReactivate.OrdinaryOperationReady);
        var operationalAfterReactivate = Assert.Single(
            await fixture.GetOperationalItemsAsync(token),
            item => item.ItemId == retired.Id);
        Assert.Null(operationalAfterReactivate.CurrentRegisteredQuantity);
        Assert.True(operationalAfterReactivate.RequiresReconciliation);

        using (var delete = await fixture.PostDeleteAsync(
                   retired.Id,
                   Guid.NewGuid(),
                   token))
        {
            delete.EnsureSuccessStatusCode();
        }

        Assert.DoesNotContain(
            retired.Id,
            (await fixture.GetConfigurationItemsAsync(token))
                .Select(item => item.ItemId));
        Assert.DoesNotContain(
            retired.Id,
            (await fixture.GetOperationalItemsAsync(token))
                .Select(item => item.ItemId));
    }

    [Fact]
    public async Task Operation_identity_reads_uninitialized_operational_state()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await fixture.CreateActorAsync(
            "operation-read",
            token,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, token);

        using var response = await fixture.Client.GetAsync(OperationsRoute, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(token));
        var returned = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(item.Id, returned.GetProperty("itemId").GetGuid());
        Assert.Equal(JsonValueKind.Null, returned
            .GetProperty("currentRegisteredQuantity").ValueKind);
        Assert.False(returned.GetProperty("quantityEstablished").GetBoolean());
        Assert.False(returned
            .GetProperty("hasNegativeBalanceInconsistency").GetBoolean());
        Assert.Equal(0, returned.GetProperty("asOfMovementRevision").GetInt64());
    }

    [Fact]
    public async Task Configuration_and_operation_do_not_imply_each_other()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await fixture.AddItemAsync("Harina", "kg", token);

        var configurationActor = await fixture.CreateActorAsync(
            "configuration-only",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(configurationActor, token);
        using var operationForbidden = await fixture.Client.GetAsync(
            OperationsRoute,
            token);
        await InventoryTestAssertions.AssertProblemAsync(
            operationForbidden,
            HttpStatusCode.Forbidden,
            "inventory.operation.forbidden",
            token);

        var operationActor = await fixture.CreateActorAsync(
            "operation-only-read",
            token,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(operationActor, token);
        using var configurationForbidden = await fixture.Client.GetAsync(
            ConfigurationRoute,
            token);
        await InventoryTestAssertions.AssertProblemAsync(
            configurationForbidden,
            HttpStatusCode.Forbidden,
            "inventory.configuration.forbidden",
            token);
    }

    [Fact]
    public async Task Identity_with_both_responsibilities_reads_both_surfaces()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await fixture.CreateActorAsync(
            "both-reads",
            token,
            FunctionalResponsibility.InventoryConfiguration,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, token);

        using var configuration = await fixture.Client.GetAsync(
            ConfigurationRoute,
            token);
        using var operations = await fixture.Client.GetAsync(OperationsRoute, token);

        Assert.Equal(HttpStatusCode.OK, configuration.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operations.StatusCode);
    }

    [Fact]
    public async Task Unrelated_authenticated_identity_has_no_inventory_read()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "unrelated-read",
            token,
            FunctionalResponsibility.GeneralConfiguration,
            FunctionalResponsibility.Preparation,
            FunctionalResponsibility.OrderOperationsAndBasicClosure);
        await fixture.LoginAsync(actor, token);

        using var configuration = await fixture.Client.GetAsync(
            ConfigurationRoute,
            token);
        using var operations = await fixture.Client.GetAsync(OperationsRoute, token);

        Assert.Equal(HttpStatusCode.Forbidden, configuration.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, operations.StatusCode);
    }

    [Theory]
    [InlineData(ConfigurationRoute)]
    [InlineData(OperationsRoute)]
    public async Task Anonymous_read_is_401(string route)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var client = fixture.CreateAnonymousClient();

        using var response = await client.GetAsync(route, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
