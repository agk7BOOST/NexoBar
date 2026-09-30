using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OrderOperations;

namespace NexoBar.OperationalConfiguration.IntegrationTests;

[Collection(OperationalConfigurationApiCollection.Name)]
public sealed partial class ConfigurationLifecycleApiTests(OperationalConfigurationApiFixture fixture)
{
    private const string Contexts = "contexts";
    private const string Destinations = "preparation-responsibilities";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Contexts)]
    [InlineData(Destinations)]
    public async Task Lifecycle_is_durable_actor_aware_and_replays_original_results_after_delete(string entity)
    {
        await fixture.ResetAsync(Token);
        var actor = await ActorAsync();
        using var client = await LoginAsync(actor);
        var createKey = Guid.NewGuid();
        var created = await CreateAsync(client, entity, "  Original  ", createKey);
        Assert.True(created.IsActive);
        var operations = new List<(string Action, object Request, Guid Key, string Result)>();
        var current = created;
        foreach (var action in new[] { "rename", "retire", "reactivate", "delete" })
        {
            object body = action == "rename"
                ? new { expectedCurrentOperationalName = current.OperationalName, expectedIsActive = current.IsActive, newOperationalName = "  Renamed  " }
                : new { expectedCurrentOperationalName = current.OperationalName, expectedIsActive = current.IsActive };
            var key = Guid.NewGuid();
            using var response = await CommandAsync(client, entity, current.Id, action, body, key);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync(Token);
            operations.Add((action, body, key, json));
            current = (await response.Content.ReadFromJsonAsync<ConfigurationLifecycleResponse>(Token))!;
            if (action == "rename") Assert.Equal("Renamed", current.OperationalName);
            if (action == "retire")
            {
                Assert.False(current.IsActive);
                var admin = await client.GetFromJsonAsync<OperationalContextReference[]>(Route(entity), Token);
                Assert.False(Assert.Single(admin!).IsActive);
                if (entity == Contexts)
                    Assert.Empty((await client.GetFromJsonAsync<OperationalContextReference[]>("/api/operational-configuration/order-contexts", Token))!);
                else
                    Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/catalog/preparation-responsibilities", Token))!);
                using var duplicate = await SendAsync(client, HttpMethod.Post, Route(entity), new { operationalName = "rEnAmEd" }, Guid.NewGuid());
                Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            }
            if (action == "reactivate") Assert.True(current.IsActive);
        }
        Assert.True(current.IsDeleted);
        Assert.Empty((await client.GetFromJsonAsync<OperationalContextReference[]>(Route(entity), Token))!);
        await fixture.RestartAsync(Token);
        using var replayClient = await LoginAsync(actor);
        await fixture.RemoveResponsibilityAsync(actor.IdentityId, FunctionalResponsibility.GeneralConfiguration, Token);
        foreach (var operation in operations)
        {
            using var replay = await CommandAsync(replayClient, entity, created.Id, operation.Action, operation.Request, operation.Key);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(operation.Result, await replay.Content.ReadAsStringAsync(Token));
            using var changed = await CommandAsync(replayClient, entity, Guid.CreateVersion7(), operation.Action, operation.Request, operation.Key);
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            using var otherKind = await CommandAsync(replayClient, entity, created.Id, operation.Action == "delete" ? "retire" : "delete", new { expectedCurrentOperationalName = "Renamed", expectedIsActive = true }, operation.Key);
            Assert.Equal(HttpStatusCode.Conflict, otherKind.StatusCode);
        }
        using var createReplay = await SendAsync(replayClient, HttpMethod.Post, Route(entity), new { operationalName = "Original" }, createKey);
        Assert.Equal(HttpStatusCode.Created, createReplay.StatusCode);
        Assert.Equal(created.Id, (await createReplay.Content.ReadFromJsonAsync<OperationalContextReference>(Token))!.Id);
        using var creationKeyOnLifecycle = await CommandAsync(replayClient, entity, created.Id, "retire", new { expectedCurrentOperationalName = "Original", expectedIsActive = true }, createKey);
        Assert.Equal(HttpStatusCode.Conflict, creationKeyOnLifecycle.StatusCode);
        using var lifecycleKeyOnCreation = await SendAsync(replayClient, HttpMethod.Post, Route(entity), new { operationalName = "Other" }, operations[0].Key);
        Assert.Equal(HttpStatusCode.Conflict, lifecycleKeyOnCreation.StatusCode);
        using var secondClient = await LoginAsync(await ActorAsync());
        foreach (var operation in operations)
        {
            using var otherActor = await CommandAsync(secondClient, entity, created.Id, operation.Action, operation.Request, operation.Key);
            Assert.Equal(HttpStatusCode.Conflict, otherActor.StatusCode);
        }
        await fixture.DeactivateAsync(actor.IdentityId, Token);
        using var inactive = await CommandAsync(replayClient, entity, created.Id, "delete", operations[^1].Request, operations[^1].Key);
        Assert.Equal(HttpStatusCode.Unauthorized, inactive.StatusCode);
    }

    [Theory]
    [InlineData(Contexts, "rename")]
    [InlineData(Contexts, "retire")]
    [InlineData(Contexts, "reactivate")]
    [InlineData(Contexts, "delete")]
    [InlineData(Destinations, "rename")]
    [InlineData(Destinations, "retire")]
    [InlineData(Destinations, "reactivate")]
    [InlineData(Destinations, "delete")]
    public async Task New_commands_require_session_general_configuration_antiforgery_and_uuid_v4(string entity, string action)
    {
        await fixture.ResetAsync(Token);
        var actor = await ActorAsync();
        using var client = await LoginAsync(actor);
        var item = await CreateAsync(client, entity, "Protected");
        object request = action == "rename"
            ? new { expectedCurrentOperationalName = item.OperationalName, expectedIsActive = true, newOperationalName = "Changed" }
            : new { expectedCurrentOperationalName = item.OperationalName, expectedIsActive = true };
        using var missingCsrf = await CommandAsync(client, entity, item.Id, action, request, Guid.NewGuid(), csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        using var invalidKey = await CommandAsync(client, entity, item.Id, action, request, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.BadRequest, invalidKey.StatusCode);
        using var anonymous = fixture.CreateClient();
        using var unauthenticated = await CommandAsync(anonymous, entity, item.Id, action, request, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        await fixture.RemoveResponsibilityAsync(actor.IdentityId, FunctionalResponsibility.GeneralConfiguration, Token);
        using var forbidden = await CommandAsync(client, entity, item.Id, action, request, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Theory]
    [InlineData(Contexts)]
    [InlineData(Destinations)]
    public async Task Stale_expectations_and_name_collisions_do_not_overwrite_state(string entity)
    {
        await fixture.ResetAsync(Token);
        using var client = await LoginAsync(await ActorAsync());
        var first = await CreateAsync(client, entity, "First");
        var second = await CreateAsync(client, entity, "Second");
        using var rename = await CommandAsync(client, entity, first.Id, "rename", new { expectedCurrentOperationalName = "First", expectedIsActive = true, newOperationalName = "Updated" }, Guid.NewGuid());
        rename.EnsureSuccessStatusCode();
        using var staleDelete = await CommandAsync(client, entity, first.Id, "delete", new { expectedCurrentOperationalName = "First", expectedIsActive = true }, Guid.NewGuid());
        await ProblemAsync(staleDelete, "concurrency_conflict");
        using var retire = await CommandAsync(client, entity, second.Id, "retire", new { expectedCurrentOperationalName = "Second", expectedIsActive = true }, Guid.NewGuid());
        retire.EnsureSuccessStatusCode();
        using var collision = await CommandAsync(client, entity, first.Id, "rename", new { expectedCurrentOperationalName = "Updated", expectedIsActive = true, newOperationalName = "sEcOnD" }, Guid.NewGuid());
        await ProblemAsync(collision, "operational_name_conflict");
        using var staleState = await CommandAsync(client, entity, second.Id, "delete", new { expectedCurrentOperationalName = "Second", expectedIsActive = true }, Guid.NewGuid());
        await ProblemAsync(staleState, "concurrency_conflict");
        using var deleteRetired = await CommandAsync(client, entity, second.Id, "delete", new { expectedCurrentOperationalName = "Second", expectedIsActive = false }, Guid.NewGuid());
        deleteRetired.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Context_retirement_is_prospective_and_delete_considers_first_confirmation_and_later_changes()
    {
        await fixture.ResetAsync(Token);
        using var client = await LoginAsync(await ActorAsync());
        var a = await CreateAsync(client, Contexts, "A");
        var b = await CreateAsync(client, Contexts, "B");
        var c = await CreateAsync(client, Contexts, "C");
        var product = await ProductAsync();
        var order = await ConfirmAsync(client, a.Id, product);
        using var rename = await CommandAsync(client, Contexts, a.Id, "rename", new { expectedCurrentOperationalName = "A", expectedIsActive = true, newOperationalName = "A renamed" }, Guid.NewGuid());
        rename.EnsureSuccessStatusCode();
        using var retired = await CommandAsync(client, Contexts, a.Id, "retire", new { expectedCurrentOperationalName = "A renamed", expectedIsActive = true }, Guid.NewGuid());
        retired.EnsureSuccessStatusCode();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            Assert.Equal("A", (await db.Orders.SingleAsync(Token)).CurrentContextOperationalName);
            Assert.Equal("A", (await db.ConfirmationHistory.SingleAsync(Token)).ConfirmedContext);
        }
        using var invalidFirst = await SendAsync(client, HttpMethod.Post, "/api/order-operations/first-confirmations", new { contextId = a.Id, items = new[] { new { productId = product, quantity = 1 } } }, Guid.NewGuid());
        await ProblemAsync(invalidFirst, "context_not_current");
        using var toB = await ChangeContextAsync(client, order, a.Id, b.Id);
        toB.EnsureSuccessStatusCode();
        using var toRetired = await ChangeContextAsync(client, order, b.Id, a.Id);
        Assert.Equal(HttpStatusCode.Conflict, toRetired.StatusCode);
        using var toC = await ChangeContextAsync(client, order, b.Id, c.Id);
        toC.EnsureSuccessStatusCode();
        using var cancelled = await SendAsync(client, HttpMethod.Post, $"/api/orders/{order}/complete-cancellation", null, Guid.NewGuid());
        cancelled.EnsureSuccessStatusCode();
        foreach (var item in new[] { a, b, c })
        {
            using var deletion = await CommandAsync(client, Contexts, item.Id, "delete", new { expectedCurrentOperationalName = item.Id == a.Id ? "A renamed" : item.OperationalName, expectedIsActive = item.Id != a.Id }, Guid.NewGuid());
            await ProblemAsync(deletion, "delete.operational_participation");
        }
    }

    [Fact]
    public async Task Destination_retire_and_delete_respect_product_references_and_product_reactivation()
    {
        await fixture.ResetAsync(Token);
        using var client = await LoginAsync(await ActorAsync());
        var destination = await CreateAsync(client, Destinations, "Kitchen");
        var product = await ProductAsync();
        await ConfigureProductAsync(client, product, null, destination.Id);
        using var blocked = await CommandAsync(client, Destinations, destination.Id, "retire", new { expectedCurrentOperationalName = "Kitchen", expectedIsActive = true }, Guid.NewGuid());
        await ProblemAsync(blocked, "retire.active_products");
        using var retireProduct = await SendAsync(client, HttpMethod.Post, $"/api/catalog/products/{product}/retire", null, Guid.NewGuid());
        retireProduct.EnsureSuccessStatusCode();
        using var retired = await CommandAsync(client, Destinations, destination.Id, "retire", new { expectedCurrentOperationalName = "Kitchen", expectedIsActive = true }, Guid.NewGuid());
        retired.EnsureSuccessStatusCode();
        using var reactivateProduct = await SendAsync(client, HttpMethod.Post, $"/api/catalog/products/{product}/reactivate", null, Guid.NewGuid());
        await ProblemAsync(reactivateProduct, "preparation_destination_not_current");
        using var deletion = await CommandAsync(client, Destinations, destination.Id, "delete", new { expectedCurrentOperationalName = "Kitchen", expectedIsActive = false }, Guid.NewGuid());
        await ProblemAsync(deletion, "delete.product_references");
        var other = await ProductAsync();
        using var invalidConfig = await SendAsync(client, HttpMethod.Post, $"/api/catalog/products/{other}/preparation-configuration-changes", new { expectedCurrentPreparationResponsibilityId = (Guid?)null, newPreparationResponsibilityId = destination.Id }, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, invalidConfig.StatusCode);
        using var reactivated = await CommandAsync(client, Destinations, destination.Id, "reactivate", new { expectedCurrentOperationalName = "Kitchen", expectedIsActive = false }, Guid.NewGuid());
        reactivated.EnsureSuccessStatusCode();
        using var validProduct = await SendAsync(client, HttpMethod.Post, $"/api/catalog/products/{product}/reactivate", null, Guid.NewGuid());
        validProduct.EnsureSuccessStatusCode();
        await ConfigureProductAsync(client, product, destination.Id, null);
        using var eligible = await CommandAsync(client, Destinations, destination.Id, "delete", new { expectedCurrentOperationalName = "Kitchen", expectedIsActive = true }, Guid.NewGuid());
        eligible.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Retired_destination_preserves_enablement_and_operable_work_and_delete_blocks_each_dependency()
    {
        await fixture.ResetAsync(Token);
        var actor = await ActorAsync();
        using var client = await LoginAsync(actor);
        var destination = await CreateAsync(client, Destinations, "Kitchen");
        var context = await CreateAsync(client, Contexts, "Context");
        var product = await ProductAsync();
        await ConfigureProductAsync(client, product, null, destination.Id);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var identities = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
            identities.PreparationEnablements.Add(new PreparationEnablement(actor.IdentityId, destination.Id));
            await identities.SaveChangesAsync(Token);
        }
        var order = await ConfirmAsync(client, context.Id, product);
        await ConfigureProductAsync(client, product, destination.Id, null);
        using var rename = await CommandAsync(client, Destinations, destination.Id, "rename", new { expectedCurrentOperationalName = "Kitchen", expectedIsActive = true, newOperationalName = "Kitchen renamed" }, Guid.NewGuid());
        rename.EnsureSuccessStatusCode();
        using var retired = await CommandAsync(client, Destinations, destination.Id, "retire", new { expectedCurrentOperationalName = "Kitchen renamed", expectedIsActive = true }, Guid.NewGuid());
        retired.EnsureSuccessStatusCode();
        var destinations = await client.GetFromJsonAsync<JsonElement[]>("/api/identity-sessions/current/preparation-destinations", Token);
        Assert.Equal("Kitchen renamed", Assert.Single(destinations!).GetProperty("operationalName").GetString());
        Guid workId;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var work = await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>().PreparationWork.SingleAsync(Token);
            Assert.Equal(destination.Id, work.PreparationResponsibilityId);
            workId = work.Id;
        }
        foreach (var action in new[] { "start", "ready" })
        {
            using var progress = await SendAsync(client, HttpMethod.Post, $"/api/order-operations/preparation/work/{workId}/{action}", new { quantity = 1 }, Guid.NewGuid());
            progress.EnsureSuccessStatusCode();
        }
        using var enabledDelete = await CommandAsync(client, Destinations, destination.Id, "delete", new { expectedCurrentOperationalName = "Kitchen renamed", expectedIsActive = false }, Guid.NewGuid());
        await ProblemAsync(enabledDelete, "delete.preparation_enablements");
        await using (var scope = fixture.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>().PreparationEnablements.ExecuteDeleteAsync(Token);
        using var historicalDelete = await CommandAsync(client, Destinations, destination.Id, "delete", new { expectedCurrentOperationalName = "Kitchen renamed", expectedIsActive = false }, Guid.NewGuid());
        await ProblemAsync(historicalDelete, "delete.operational_participation");
        // Even corrupted current Product configuration cannot originate new Work toward a retired destination.
        await using (var scope = fixture.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE catalog.products SET requires_preparation = true, preparation_responsibility_id = {destination.Id} WHERE id = {product}", Token);
        using var invalid = await SendAsync(client, HttpMethod.Post, "/api/order-operations/first-confirmations", new { contextId = context.Id, items = new[] { new { productId = product, quantity = 1 } } }, Guid.NewGuid());
        await ProblemAsync(invalid, "preparation_destination_not_current");
        using var pending = await SendAsync(client, HttpMethod.Post, $"/api/orders/{order}/pending-composition", null, Guid.NewGuid());
        pending.EnsureSuccessStatusCode();
        var pendingId = (await pending.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("pendingCompositionId").GetGuid();
        using var subsequent = await SendAsync(client, HttpMethod.Post, $"/api/order-operations/orders/{order}/confirmations", new { pendingCompositionId = pendingId, items = new[] { new { productId = product, quantity = 1 } } }, Guid.NewGuid());
        await ProblemAsync(subsequent, "preparation_destination_not_current");
    }

    [Fact]
    public async Task Migration_up_down_preserves_legacy_state_and_refuses_to_destroy_durable_lifecycle_results()
    {
        await fixture.ResetAsync(Token);
        using var client = await LoginAsync(await ActorAsync());
        var context = await CreateAsync(client, Contexts, "Legacy Context");
        var destination = await CreateAsync(client, Destinations, "Legacy Destination");
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OperationalConfigurationDbContext>();
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260922130000_AddOperationalContexts", Token);
        await migrator.MigrateAsync(cancellationToken: Token);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.True((await db.Contexts.AsNoTracking().SingleAsync(Token)).IsActive);
        Assert.True((await db.PreparationResponsibilities.AsNoTracking().SingleAsync(Token)).IsActive);
        using var deletion = await CommandAsync(client, Contexts, context.Id, "delete", new { expectedCurrentOperationalName = context.OperationalName, expectedIsActive = true }, Guid.NewGuid());
        deletion.EnsureSuccessStatusCode();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync("20260922130000_AddOperationalContexts", Token));
        Assert.Single(await db.OperationalContextLifecycleCommands.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(destination.Id, (await db.PreparationResponsibilities.AsNoTracking().SingleAsync(Token)).Id);
    }

    private async Task<TestActor> ActorAsync()
    {
        var actor = await fixture.CreateActorAsync($"Lifecycle {Guid.NewGuid():N}", true, FunctionalResponsibility.GeneralConfiguration, Token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        foreach (var responsibility in new[] { FunctionalResponsibility.CatalogConfiguration, FunctionalResponsibility.OrderOperationsAndBasicClosure, FunctionalResponsibility.Preparation })
            db.ResponsibilityAssignments.Add(new ResponsibilityAssignment(actor.IdentityId, responsibility));
        await db.SaveChangesAsync(Token);
        return actor;
    }
    private async Task<HttpClient> LoginAsync(TestActor actor)
    {
        var client = fixture.CreateClient();
        using var response = await SendAsync(client, HttpMethod.Post, "/api/identity-sessions", new { loginIdentifier = actor.LoginIdentifier, secret = actor.Secret }, null);
        response.EnsureSuccessStatusCode();
        return client;
    }
    private static string Route(string entity) => $"/api/operational-configuration/{entity}";
    private async Task<ConfigurationLifecycleResponse> CreateAsync(HttpClient client, string entity, string name, Guid? key = null)
    {
        using var response = await SendAsync(client, HttpMethod.Post, Route(entity), new { operationalName = name }, key ?? Guid.NewGuid());
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<OperationalContextReference>(Token))!;
        return new(result.Id, result.OperationalName, result.IsActive, false);
    }
    private async Task<HttpResponseMessage> CommandAsync(HttpClient client, string entity, Guid id, string action, object body, Guid key, bool csrf = true) =>
        await SendAsync(client, action == "delete" ? HttpMethod.Delete : HttpMethod.Post,
            $"{Route(entity)}/{id}" + (action == "delete" ? "" : "/" + (action == "rename" ? "operational-name-changes" : action)), body, key, csrf);
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body, Guid? key, bool csrf = true)
    {
        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (key is Guid value) request.Headers.Add("Idempotency-Key", value.ToString());
        if (csrf)
        {
            using var response = await client.GetAsync("/api/security/antiforgery", Token);
            var document = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
            request.Headers.Add("X-NexoBar-CSRF", document.GetProperty("requestToken").GetString());
        }
        return await client.SendAsync(request, Token);
    }
    private static async Task ProblemAsync(HttpResponseMessage response, string suffix)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.EndsWith(suffix, problem.GetProperty("code").GetString());
    }
    private async Task<Guid> ProductAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var product = new Product(Guid.CreateVersion7(), $"Product {Guid.NewGuid():N}", 5);
        db.Products.Add(product);
        await db.SaveChangesAsync(Token);
        return product.Id;
    }
    private static async Task<Guid> ConfirmAsync(HttpClient client, Guid context, Guid product)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/api/order-operations/first-confirmations", new { contextId = context, items = new[] { new { productId = product, quantity = 1 } } }, Guid.NewGuid());
        response.EnsureSuccessStatusCode();
        return Guid.Parse((await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("operationalReference").GetString()!);
    }
    private static async Task ConfigureProductAsync(HttpClient client, Guid product, Guid? expected, Guid? destination)
    {
        using var response = await SendAsync(client, HttpMethod.Post, $"/api/catalog/products/{product}/preparation-configuration-changes", new { expectedCurrentPreparationResponsibilityId = expected, newPreparationResponsibilityId = destination }, Guid.NewGuid());
        response.EnsureSuccessStatusCode();
    }
    private static Task<HttpResponseMessage> ChangeContextAsync(HttpClient client, Guid order, Guid expected, Guid target) =>
        SendAsync(client, HttpMethod.Post, $"/api/order-operations/orders/{order}/context-changes", new { expectedCurrentContextId = expected, newContextId = target }, Guid.NewGuid());
}
