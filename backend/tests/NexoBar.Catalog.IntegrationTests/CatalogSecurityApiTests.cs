using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.Catalog.IntegrationTests;

[Collection(CatalogApiCollection.Name)]
public sealed class CatalogSecurityApiTests(CatalogApiFixture fixture)
{
    private const string Products = "/api/catalog/products";
    private const string OperationalProducts = "/api/catalog/operational-products";
    private const string PreparationResponsibilities =
        "/api/catalog/preparation-responsibilities";

    [Fact]
    public async Task Administrative_read_requires_catalog_configuration()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        using var anonymous = fixture.CreateUnauthenticatedClient();
        using var anonymousResponse = await anonymous.GetAsync(Products, token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var operations = await fixture.CreateActorAsync(
            "Operations", [FunctionalResponsibility.OrderOperationsAndBasicClosure], token);
        using var operationsClient = await fixture.CreateAuthenticatedClientAsync(operations);
        using var forbidden = await operationsClient.GetAsync(Products, token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var catalog = await fixture.CreateActorAsync(
            "Catalog", [FunctionalResponsibility.CatalogConfiguration], token);
        using var catalogClient = await fixture.CreateAuthenticatedClientAsync(catalog);
        using var allowed = await catalogClient.GetAsync(Products, token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Operational_read_has_a_narrow_capability_specific_contract()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var available = await fixture.CreateProductAsync("Available", "4", token);
        var unavailable = await fixture.CreateProductAsync("Unavailable", "5", token);
        await fixture.SetProductAvailableAsync(unavailable.Id, false, token);

        using var anonymous = fixture.CreateUnauthenticatedClient();
        using var anonymousResponse = await anonymous.GetAsync(OperationalProducts, token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var catalog = await fixture.CreateActorAsync(
            "Catalog", [FunctionalResponsibility.CatalogConfiguration], token);
        var intervention = await fixture.CreateActorAsync(
            "Intervention", [FunctionalResponsibility.OperationalIntervention], token);
        var operations = await fixture.CreateActorAsync(
            "Operations", [FunctionalResponsibility.OrderOperationsAndBasicClosure], token);
        var dual = await fixture.CreateActorAsync(
            "Dual", [
                FunctionalResponsibility.OrderOperationsAndBasicClosure,
                FunctionalResponsibility.OperationalIntervention], token);
        using var catalogClient = await fixture.CreateAuthenticatedClientAsync(catalog);
        using var interventionClient = await fixture.CreateAuthenticatedClientAsync(intervention);
        using var operationsClient = await fixture.CreateAuthenticatedClientAsync(operations);
        using var dualClient = await fixture.CreateAuthenticatedClientAsync(dual);
        using var catalogResponse = await catalogClient.GetAsync(OperationalProducts, token);
        using var interventionResponse = await interventionClient.GetAsync(OperationalProducts, token);
        Assert.Equal(HttpStatusCode.Forbidden, catalogResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, interventionResponse.StatusCode);

        using var normal = await operationsClient.GetAsync(OperationalProducts, token);
        normal.EnsureSuccessStatusCode();
        using var normalJson = JsonDocument.Parse(await normal.Content.ReadAsStreamAsync(token));
        var normalProducts = normalJson.RootElement.EnumerateArray().ToArray();
        Assert.Equal(available.Id, Assert.Single(normalProducts).GetProperty("id").GetGuid());
        Assert.DoesNotContain(normalProducts, product =>
            product.GetProperty("id").GetGuid() == unavailable.Id);
        Assert.All(normalProducts, product => Assert.Equal(4, product.EnumerateObject().Count()));

        using var privileged = await dualClient.GetAsync(OperationalProducts, token);
        privileged.EnsureSuccessStatusCode();
        using var privilegedJson = JsonDocument.Parse(await privileged.Content.ReadAsStreamAsync(token));
        var unavailableProduct = privilegedJson.RootElement.EnumerateArray().Single(
            product => product.GetProperty("id").GetGuid() == unavailable.Id);
        Assert.False(unavailableProduct.GetProperty("isAvailable").GetBoolean());
    }

    [Fact]
    public async Task Preparation_responsibility_lookup_is_catalog_scoped_and_minimal()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var expected = await fixture.CreatePreparationResponsibilityAsync("Kitchen", token);
        using var anonymous = fixture.CreateUnauthenticatedClient();
        using var anonymousResponse = await anonymous.GetAsync(PreparationResponsibilities, token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var general = await fixture.CreateActorAsync(
            "General", [FunctionalResponsibility.GeneralConfiguration], token);
        var operations = await fixture.CreateActorAsync(
            "Operations", [FunctionalResponsibility.OrderOperationsAndBasicClosure], token);
        var catalog = await fixture.CreateActorAsync(
            "Catalog", [FunctionalResponsibility.CatalogConfiguration], token);
        using var generalClient = await fixture.CreateAuthenticatedClientAsync(general);
        using var operationsClient = await fixture.CreateAuthenticatedClientAsync(operations);
        using var catalogClient = await fixture.CreateAuthenticatedClientAsync(catalog);
        using var generalResponse = await generalClient.GetAsync(PreparationResponsibilities, token);
        using var operationsResponse = await operationsClient.GetAsync(PreparationResponsibilities, token);
        Assert.Equal(HttpStatusCode.Forbidden, generalResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, operationsResponse.StatusCode);
        using var allowed = await catalogClient.GetAsync(PreparationResponsibilities, token);
        allowed.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await allowed.Content.ReadAsStreamAsync(token));
        var item = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(expected.Id, item.GetProperty("id").GetGuid());
        Assert.Equal("Kitchen", item.GetProperty("operationalName").GetString());
        Assert.Equal(2, item.EnumerateObject().Count());
    }

    [Fact]
    public async Task Catalog_writes_use_antiforgery_actor_aware_replay_and_current_authority()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var actor = await fixture.CreateActorAsync(
            "Catalog", [FunctionalResponsibility.CatalogConfiguration], token);
        var other = await fixture.CreateActorAsync(
            "Other", [FunctionalResponsibility.CatalogConfiguration], token);
        var wrong = await fixture.CreateActorAsync(
            "Operations", [FunctionalResponsibility.OrderOperationsAndBasicClosure], token);
        using var client = await fixture.CreateAuthenticatedClientAsync(actor);
        using var otherClient = await fixture.CreateAuthenticatedClientAsync(other);
        using var wrongClient = await fixture.CreateAuthenticatedClientAsync(wrong);
        using var anonymous = fixture.CreateUnauthenticatedClient();
        using var anonymousRejected = await CreateAsync(
            anonymous, Guid.NewGuid(), "Anonymous", "3", token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRejected.StatusCode);
        using var forbidden = await CreateAsync(
            wrongClient, Guid.NewGuid(), "Wrong", "3", token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var key = Guid.NewGuid();
        client.DefaultRequestHeaders.Remove("X-NexoBar-CSRF");
        using var missingAntiforgery = await CreateAsync(client, key, "Kitchen", "3", token);
        Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
        client.DefaultRequestHeaders.Add("X-NexoBar-CSRF", "invalid");
        using var invalidAntiforgery = await CreateAsync(client, key, "Kitchen", "3", token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidAntiforgery.StatusCode);
        client.DefaultRequestHeaders.Remove("X-NexoBar-CSRF");
        await fixture.AuthenticateAsync(client, actor.LoginIdentifier, actor.Secret);
        using var created = await CreateAsync(client, key, "Kitchen", "3", token);
        created.EnsureSuccessStatusCode();
        Assert.Equal(actor.IdentityId, await fixture.ReadCreationCommandActorAsync(key, token));
        using var replay = await CreateAsync(client, key, "Kitchen", "3", token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using var differentActor = await CreateAsync(otherClient, key, "Kitchen", "3", token);
        Assert.Equal(HttpStatusCode.Conflict, differentActor.StatusCode);
        using var differentIntent = await CreateAsync(client, key, "Bar", "3", token);
        Assert.Equal(HttpStatusCode.Conflict, differentIntent.StatusCode);

        var product = Assert.IsType<ProductResponse>(
            await created.Content.ReadFromJsonAsync<ProductResponse>(token));
        using var price = await ChangePriceAsync(client, product.Id, "3", "4", token);
        Assert.Equal(HttpStatusCode.OK, price.StatusCode);
        var responsibility = await fixture.CreatePreparationResponsibilityAsync("Kitchen responsibility", token);
        using var preparation = await ChangePreparationAsync(
            client, product.Id, null, responsibility.Id, token);
        Assert.Equal(HttpStatusCode.OK, preparation.StatusCode);

        await fixture.RemoveResponsibilityAsync(
            actor.IdentityId, FunctionalResponsibility.CatalogConfiguration, token);
        using var rejectedNew = await CreateAsync(client, Guid.NewGuid(), "After revoke", "2", token);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedNew.StatusCode);
        using var replayAfterRevocation = await CreateAsync(client, key, "Kitchen", "3", token);
        Assert.Equal(HttpStatusCode.Created, replayAfterRevocation.StatusCode);
    }

    [Fact]
    public async Task Actor_migration_preserves_legacy_null_and_round_trips_down()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var key = Guid.NewGuid();
        await fixture.InsertLegacyCreationCommandAsync(
            key, Guid.CreateVersion7(), "Legacy", token);
        Assert.Null(await fixture.ReadCreationCommandActorAsync(key, token));
        await fixture.VerifyActorMigrationRoundTripAsync(token);
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    [Fact]
    public async Task Internal_catalog_collaborations_remain_independent_of_catalog_http_authority()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Snapshot", "7", token);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var confirmation = scope.ServiceProvider
            .GetRequiredService<IOrderConfirmationCatalog>();
        var references = scope.ServiceProvider
            .GetRequiredService<IProductOperationalReferenceLookup>();
        var snapshot = Assert.Single(await confirmation.ReadProductsAsync(
            [product.Id], transaction, token));
        var reference = Assert.Single(await references.ReadByIdsAsync(
            [product.Id], transaction, token));
        Assert.Equal(7m, snapshot.Price);
        Assert.Equal("Snapshot", snapshot.OperationalName);
        Assert.Equal(product.Id, reference.ProductId);
        Assert.Equal("Snapshot", reference.OperationalName);
    }

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client, Guid key, string name, string price, CancellationToken token) =>
        SendAsync(client, HttpMethod.Post, Products, key,
            new { operationalName = name, price, requiresPreparation = false }, token);

    private static Task<HttpResponseMessage> ChangePriceAsync(
        HttpClient client, Guid id, string expected, string price, CancellationToken token) =>
        SendAsync(client, HttpMethod.Post, $"{Products}/{id:D}/price-changes", Guid.NewGuid(),
            new { expectedCurrentPrice = expected, newPrice = price }, token);

    private static Task<HttpResponseMessage> ChangePreparationAsync(
        HttpClient client, Guid id, Guid? expected, Guid? next, CancellationToken token) =>
        SendAsync(client, HttpMethod.Post,
            $"{Products}/{id:D}/preparation-configuration-changes", Guid.NewGuid(),
            new
            {
                expectedCurrentPreparationResponsibilityId = expected,
                newPreparationResponsibilityId = next
            }, token);

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string route, Guid key, object body,
        CancellationToken token)
    {
        var request = new HttpRequestMessage(method, route) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return client.SendAsync(request, token);
    }
}
