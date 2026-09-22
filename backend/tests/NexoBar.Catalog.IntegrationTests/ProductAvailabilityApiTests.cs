using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Catalog.IntegrationTests;

[Collection(CatalogApiCollection.Name)]
public sealed class ProductAvailabilityApiTests(CatalogApiFixture fixture)
{
    private const string Products = "/api/catalog/products";
    private const string AvailabilityProducts =
        "/api/catalog/availability-administration-products";

    [Fact]
    public async Task Availability_read_is_narrow_and_intervention_scoped()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var available = await fixture.CreateProductAsync("Available", "4", token);
        var unavailable = await fixture.CreateProductAsync("Unavailable", "5", token);
        await fixture.SetProductAvailableAsync(unavailable.Id, false, token);
        var retired = await fixture.CreateProductAsync("Retired", "6", token);
        await fixture.SetProductActiveAsync(retired.Id, false, token);

        var intervention = await fixture.CreateActorAsync(
            "Availability Intervention", [FunctionalResponsibility.OperationalIntervention], token);
        var operations = await fixture.CreateActorAsync(
            "Availability Operations", [FunctionalResponsibility.OrderOperationsAndBasicClosure], token);
        var catalog = await fixture.CreateActorAsync(
            "Availability Catalog", [FunctionalResponsibility.CatalogConfiguration], token);
        using var interventionClient = await fixture.CreateAuthenticatedClientAsync(intervention);
        using var operationsClient = await fixture.CreateAuthenticatedClientAsync(operations);
        using var catalogClient = await fixture.CreateAuthenticatedClientAsync(catalog);

        using var allowed = await interventionClient.GetAsync(AvailabilityProducts, token);
        allowed.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await allowed.Content.ReadAsStreamAsync(token));
        var products = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, products.Length);
        Assert.Contains(products, product => product.GetProperty("id").GetGuid() == available.Id);
        Assert.Contains(products, product =>
            product.GetProperty("id").GetGuid() == unavailable.Id &&
            !product.GetProperty("isAvailable").GetBoolean());
        Assert.DoesNotContain(products, product => product.GetProperty("id").GetGuid() == retired.Id);
        Assert.All(products, product => Assert.Equal(3, product.EnumerateObject().Count()));

        using var operationsResponse = await operationsClient.GetAsync(AvailabilityProducts, token);
        using var catalogResponse = await catalogClient.GetAsync(AvailabilityProducts, token);
        Assert.Equal(HttpStatusCode.Forbidden, operationsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, catalogResponse.StatusCode);
    }

    [Fact]
    public async Task Availability_command_transitions_noop_and_rejects_stale_or_retired()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Availability", "7", token);
        var intervention = await fixture.CreateActorAsync(
            "Availability Mutator", [FunctionalResponsibility.OperationalIntervention], token);
        using var client = await fixture.CreateAuthenticatedClientAsync(intervention);

        using var missing = await SendAvailabilityAsync(
            client, Guid.NewGuid(), Guid.NewGuid(), true, false, token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var unavailableKey = Guid.NewGuid();
        using var unavailable = await SendAvailabilityAsync(
            client, product.Id, unavailableKey, true, false, token);
        unavailable.EnsureSuccessStatusCode();
        Assert.False((await unavailable.Content.ReadFromJsonAsync<ProductAvailabilityResponse>(token))!.IsAvailable);

        var noopKey = Guid.NewGuid();
        using var noop = await SendAvailabilityAsync(
            client, product.Id, noopKey, false, false, token);
        noop.EnsureSuccessStatusCode();
        Assert.False((await noop.Content.ReadFromJsonAsync<ProductAvailabilityResponse>(token))!.IsAvailable);

        using var stale = await SendAvailabilityAsync(
            client, product.Id, Guid.NewGuid(), true, false, token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var staleJson = JsonDocument.Parse(await stale.Content.ReadAsStreamAsync(token));
        Assert.Equal(
            "catalog.product.availability_concurrency_conflict",
            staleJson.RootElement.GetProperty("code").GetString());
        Assert.False(staleJson.RootElement.GetProperty("currentAvailability").GetBoolean());

        await fixture.SetProductActiveAsync(product.Id, false, token);
        using var retired = await SendAvailabilityAsync(
            client, product.Id, Guid.NewGuid(), false, true, token);
        Assert.Equal(HttpStatusCode.Conflict, retired.StatusCode);
        using var retiredJson = JsonDocument.Parse(await retired.Content.ReadAsStreamAsync(token));
        Assert.Equal("catalog.product.not_current", retiredJson.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Availability_command_has_actor_aware_replay_and_authority_boundary()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Replay Availability", "8", token);
        var intervention = await fixture.CreateActorAsync(
            "Replay Intervention", [FunctionalResponsibility.OperationalIntervention], token);
        var otherIntervention = await fixture.CreateActorAsync(
            "Other Intervention", [FunctionalResponsibility.OperationalIntervention], token);
        var catalog = await fixture.CreateActorAsync(
            "Wrong Catalog", [FunctionalResponsibility.CatalogConfiguration], token);
        var operations = await fixture.CreateActorAsync(
            "Wrong Operations", [FunctionalResponsibility.OrderOperationsAndBasicClosure], token);
        using var client = await fixture.CreateAuthenticatedClientAsync(intervention);
        using var otherClient = await fixture.CreateAuthenticatedClientAsync(otherIntervention);
        using var catalogClient = await fixture.CreateAuthenticatedClientAsync(catalog);
        using var operationsClient = await fixture.CreateAuthenticatedClientAsync(operations);

        var key = Guid.NewGuid();
        using var changed = await SendAvailabilityAsync(client, product.Id, key, true, false, token);
        changed.EnsureSuccessStatusCode();
        await fixture.RemoveResponsibilityAsync(
            intervention.IdentityId, FunctionalResponsibility.OperationalIntervention, token);
        using var replay = await SendAvailabilityAsync(client, product.Id, key, true, false, token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var other = await SendAvailabilityAsync(otherClient, product.Id, key, true, false, token);
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        using var changedIntent = await SendAvailabilityAsync(
            client, product.Id, key, false, true, token);
        Assert.Equal(HttpStatusCode.Conflict, changedIntent.StatusCode);

        using var catalogRejected = await SendAvailabilityAsync(
            catalogClient, product.Id, Guid.NewGuid(), false, true, token);
        Assert.Equal(HttpStatusCode.Forbidden, catalogRejected.StatusCode);
        using var operationsRejected = await SendAvailabilityAsync(
            operationsClient, product.Id, Guid.NewGuid(), false, true, token);
        Assert.Equal(HttpStatusCode.Forbidden, operationsRejected.StatusCode);

        var csrf = client.DefaultRequestHeaders.GetValues("X-NexoBar-CSRF").Single();
        client.DefaultRequestHeaders.Remove("X-NexoBar-CSRF");
        using var antiforgeryRejected = await SendAvailabilityAsync(
            client, product.Id, Guid.NewGuid(), false, true, token);
        Assert.Equal(HttpStatusCode.BadRequest, antiforgeryRejected.StatusCode);
        client.DefaultRequestHeaders.Add("X-NexoBar-CSRF", csrf);

        var invalidKey = "not-a-uuid";
        using var invalid = await SendAvailabilityAsync(
            client, product.Id, invalidKey, false, true, token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Two_observers_with_true_expected_allow_one_commit_and_one_stale_conflict()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Availability concurrency", "10", token);
        var firstActor = await fixture.CreateActorAsync(
            "First Availability", [FunctionalResponsibility.OperationalIntervention], token);
        var secondActor = await fixture.CreateActorAsync(
            "Second Availability", [FunctionalResponsibility.OperationalIntervention], token);
        using var firstClient = await fixture.CreateAuthenticatedClientAsync(firstActor);
        using var secondClient = await fixture.CreateAuthenticatedClientAsync(secondActor);

        using var first = await SendAvailabilityAsync(
            firstClient, product.Id, Guid.NewGuid(), true, false, token);
        first.EnsureSuccessStatusCode();
        using var second = await SendAvailabilityAsync(
            secondClient, product.Id, Guid.NewGuid(), true, true, token);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStreamAsync(token));
        Assert.Equal(
            "catalog.product.availability_concurrency_conflict",
            secondJson.RootElement.GetProperty("code").GetString());
        Assert.False(secondJson.RootElement.GetProperty("currentAvailability").GetBoolean());
    }

    [Fact]
    public async Task Availability_command_migration_round_trips_without_changing_product_state()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Migration Availability", "9", token);
        var previousMigration = "20260922034151_AddCatalogGroupsAndProductLifecycle";
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync(previousMigration, token);
        Assert.Equal(
            0,
            await AvailabilityTableCountAsync(db, token));
        var preserved = await db.Products.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == product.Id, token);
        Assert.True(preserved.IsActive);
        Assert.True(preserved.IsAvailable);
        Assert.Equal(9m, preserved.Price);

        await migrator.MigrateAsync(cancellationToken: token);
        Assert.Equal(
            1,
            await AvailabilityTableCountAsync(db, token));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task<int> AvailabilityTableCountAsync(
        CatalogDbContext db,
        CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT count(*)::integer
            FROM information_schema.tables
            WHERE table_schema = 'catalog'
              AND table_name = 'product_availability_change_commands'
            """;
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync(token);
        }
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    private static Task<HttpResponseMessage> SendAvailabilityAsync(
        HttpClient client,
        Guid productId,
        Guid key,
        bool expected,
        bool next,
        CancellationToken token) => SendAvailabilityAsync(
            client, productId, key.ToString("D"), expected, next, token);

    private static async Task<HttpResponseMessage> SendAvailabilityAsync(
        HttpClient client,
        Guid productId,
        string key,
        bool expected,
        bool next,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{Products}/{productId:D}/availability-changes")
        {
            Content = JsonContent.Create(new
            {
                expectedCurrentAvailability = expected,
                newAvailability = next
            })
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, token);
    }
}
