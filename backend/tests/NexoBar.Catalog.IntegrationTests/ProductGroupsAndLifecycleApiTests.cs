using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.Catalog.IntegrationTests;

[Collection(CatalogApiCollection.Name)]
public sealed class ProductGroupsAndLifecycleApiTests(CatalogApiFixture fixture)
{
    private const string Products = "/api/catalog/products";
    private const string Groups = "/api/catalog/groups";

    public static IEnumerable<object[]> CommandFamilies() =>
        [
            ["CreateGroup"], ["ChangeProductGroup"], ["RenameProduct"],
            ["RetireProduct"], ["ReactivateProduct"]
        ];

    [Theory]
    [MemberData(nameof(CommandFamilies))]
    public async Task Durable_command_replays_after_catalog_configuration_revocation(string family)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var actor = await fixture.CreateActorAsync(family + "Replay", [FunctionalResponsibility.CatalogConfiguration], token);
        using var client = await fixture.CreateAuthenticatedClientAsync(actor);
        var command = await PrepareCommandAsync(family, client, token);
        using (var initial = await SendAsync(client, command.Method, command.Route, command.Key, command.Body, token))
            Assert.Equal(command.SuccessStatus, initial.StatusCode);
        await fixture.RemoveResponsibilityAsync(actor.IdentityId, FunctionalResponsibility.CatalogConfiguration, token);
        using var replay = await SendAsync(client, command.Method, command.Route, command.Key, command.Body, token);
        Assert.Equal(command.SuccessStatus, replay.StatusCode);
    }

    [Theory]
    [MemberData(nameof(CommandFamilies))]
    public async Task Durable_command_key_cannot_be_reused_by_a_different_actor(string family)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var actorA = await fixture.CreateActorAsync(family + "A", [FunctionalResponsibility.CatalogConfiguration], token);
        var actorB = await fixture.CreateActorAsync(family + "B", [FunctionalResponsibility.CatalogConfiguration], token);
        using var clientA = await fixture.CreateAuthenticatedClientAsync(actorA);
        using var clientB = await fixture.CreateAuthenticatedClientAsync(actorB);
        var command = await PrepareCommandAsync(family, clientA, token);
        using (var initial = await SendAsync(clientA, command.Method, command.Route, command.Key, command.Body, token))
            Assert.Equal(command.SuccessStatus, initial.StatusCode);
        using var conflict = await SendAsync(clientB, command.Method, command.Route, command.Key, command.Body, token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("idempotency_key_conflict", await conflict.Content.ReadAsStringAsync(token));
    }

    [Theory]
    [MemberData(nameof(CommandFamilies))]
    public async Task Durable_command_rejects_changed_intent_for_same_actor_and_key(string family)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var actor = await fixture.CreateActorAsync(family + "Intent", [FunctionalResponsibility.CatalogConfiguration], token);
        using var client = await fixture.CreateAuthenticatedClientAsync(actor);
        var command = await PrepareCommandAsync(family, client, token);
        using (var initial = await SendAsync(client, command.Method, command.Route, command.Key, command.Body, token))
            Assert.Equal(command.SuccessStatus, initial.StatusCode);
        var changed = await PrepareChangedCommandAsync(family, command, token);
        using var conflict = await SendAsync(client, changed.Method, changed.Route, command.Key, changed.Body, token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("idempotency_key_conflict", await conflict.Content.ReadAsStringAsync(token));
    }

    [Fact]
    public async Task Creates_lists_and_replays_trimmed_case_insensitive_groups()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var key = Guid.NewGuid();
        using var created = await SendAsync(HttpMethod.Post, Groups, key, new { operationalName = "  Bebidas  " }, token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var group = Assert.IsType<GroupResponse>(await created.Content.ReadFromJsonAsync<GroupResponse>(token));
        Assert.Equal("Bebidas", group.OperationalName);
        using var replay = await SendAsync(HttpMethod.Post, Groups, key, new { operationalName = "  Bebidas  " }, token);
        Assert.Equal(group, await replay.Content.ReadFromJsonAsync<GroupResponse>(token));
        using var duplicate = await SendAsync(HttpMethod.Post, Groups, Guid.NewGuid(), new { operationalName = "bebidas" }, token);
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "catalog.group.operational_name_conflict", token);
        using var listed = await fixture.Client.GetAsync(Groups, token);
        Assert.Equal(group, Assert.Single((await listed.Content.ReadFromJsonAsync<GroupResponse[]>(token))!));
    }

    [Fact]
    public async Task Changes_Product_Group_with_nullable_expected_current_guard()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "2", token);
        var a = await CreateGroupAsync("Bebidas", token);
        var b = await CreateGroupAsync("Especiales", token);
        using var assign = await SendAsync(HttpMethod.Post, $"{Products}/{product.Id:D}/group-changes", Guid.NewGuid(), new { expectedCurrentGroupId = (Guid?)null, newGroupId = a.Id }, token);
        Assert.Equal(new ProductGroupResponse(product.Id, a.Id), await assign.Content.ReadFromJsonAsync<ProductGroupResponse>(token));
        using var stale = await SendAsync(HttpMethod.Post, $"{Products}/{product.Id:D}/group-changes", Guid.NewGuid(), new { expectedCurrentGroupId = (Guid?)null, newGroupId = b.Id }, token);
        using var problem = await AssertProblemAsync(stale, HttpStatusCode.Conflict, "catalog.product.group_concurrency_conflict", token);
        Assert.Equal(a.Id, problem.RootElement.GetProperty("currentGroupId").GetGuid());
        using var change = await SendAsync(HttpMethod.Post, $"{Products}/{product.Id:D}/group-changes", Guid.NewGuid(), new { expectedCurrentGroupId = a.Id, newGroupId = b.Id }, token);
        Assert.Equal(new ProductGroupResponse(product.Id, b.Id), await change.Content.ReadFromJsonAsync<ProductGroupResponse>(token));
        using var unassign = await SendAsync(HttpMethod.Post, $"{Products}/{product.Id:D}/group-changes", Guid.NewGuid(), new { expectedCurrentGroupId = b.Id, newGroupId = (Guid?)null }, token);
        Assert.Equal(new ProductGroupResponse(product.Id, null), await unassign.Content.ReadFromJsonAsync<ProductGroupResponse>(token));
    }

    [Fact]
    public async Task Retire_keeps_admin_state_and_reactivate_forces_available()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "2", token);
        var group = await CreateGroupAsync("Bebidas", token);
        using (var assign = await SendAsync(HttpMethod.Post, $"{Products}/{product.Id:D}/group-changes", Guid.NewGuid(), new { expectedCurrentGroupId = (Guid?)null, newGroupId = group.Id }, token)) assign.EnsureSuccessStatusCode();
        await fixture.SetProductAvailableAsync(product.Id, false, token);
        var retireKey = Guid.NewGuid();
        using var retired = await SendAsync(HttpMethod.Post, $"{Products}/{product.Id:D}/retire", retireKey, null, token);
        Assert.Equal(new ProductLifecycleResponse(product.Id, false, false), await retired.Content.ReadFromJsonAsync<ProductLifecycleResponse>(token));
        using var admin = await fixture.Client.GetAsync($"{Products}/{product.Id:D}", token);
        var retiredProduct = Assert.IsType<ProductResponse>(await admin.Content.ReadFromJsonAsync<ProductResponse>(token));
        Assert.False(retiredProduct.IsActive); Assert.False(retiredProduct.IsAvailable); Assert.Equal(group.Id, retiredProduct.GroupId);
        using var reactivated = await SendAsync(HttpMethod.Post, $"{Products}/{product.Id:D}/reactivate", Guid.NewGuid(), null, token);
        Assert.Equal(new ProductLifecycleResponse(product.Id, true, true), await reactivated.Content.ReadFromJsonAsync<ProductLifecycleResponse>(token));
        using var final = await fixture.Client.GetAsync($"{Products}/{product.Id:D}", token);
        var finalProduct = Assert.IsType<ProductResponse>(await final.Content.ReadFromJsonAsync<ProductResponse>(token));
        Assert.True(finalProduct.IsActive); Assert.True(finalProduct.IsAvailable); Assert.Equal(group.Id, finalProduct.GroupId);
    }

    [Fact]
    public async Task Retired_Product_may_share_active_name_but_reactivation_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var old = await fixture.CreateProductAsync("Agua", "2", token);
        using (var retire = await SendAsync(HttpMethod.Post, $"{Products}/{old.Id:D}/retire", Guid.NewGuid(), null, token)) retire.EnsureSuccessStatusCode();
        var current = await fixture.CreateProductAsync("Agua", "3", token);
        using (var rename = await SendAsync(HttpMethod.Post, $"{Products}/{old.Id:D}/operational-name-changes", Guid.NewGuid(), new { expectedCurrentOperationalName = "Agua", newOperationalName = "AGUA" }, token)) rename.EnsureSuccessStatusCode();
        using var reactivate = await SendAsync(HttpMethod.Post, $"{Products}/{old.Id:D}/reactivate", Guid.NewGuid(), null, token);
        await AssertProblemAsync(reactivate, HttpStatusCode.Conflict, "catalog.product.reactivation_name_conflict", token);
        Assert.NotEqual(old.Id, current.Id);
    }

    [Fact]
    public async Task Rename_uses_expected_name_and_active_unique_index()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "2", token);
        using var renamed = await SendAsync(HttpMethod.Post, $"{Products}/{product.Id:D}/operational-name-changes", Guid.NewGuid(), new { expectedCurrentOperationalName = "Agua", newOperationalName = "Agua mineral" }, token);
        Assert.Equal(new ProductOperationalNameResponse(product.Id, "Agua mineral"), await renamed.Content.ReadFromJsonAsync<ProductOperationalNameResponse>(token));
        using var stale = await SendAsync(HttpMethod.Post, $"{Products}/{product.Id:D}/operational-name-changes", Guid.NewGuid(), new { expectedCurrentOperationalName = "Agua", newOperationalName = "Otra" }, token);
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "catalog.product.operational_name_concurrency_conflict", token);
    }

    [Fact]
    public async Task Catalog_model_has_no_pending_changes_after_groups_and_lifecycle_migration()
    {
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    [Fact]
    public async Task Groups_and_lifecycle_migration_round_trips_real_postgresql_boundary()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Legacy", "2", token);
        const string previous = "20260919140000_AddCatalogCommandActors";

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database;
            await database.GetService<IMigrator>().MigrateAsync(previous, cancellationToken: token);
        }

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(token);
            await using var seedCheck = connection.CreateCommand();
            seedCheck.CommandText = "SELECT COUNT(*) FROM catalog.products WHERE id = $1";
            seedCheck.Parameters.AddWithValue(product.Id);
            Assert.Equal(1L, (long)(await seedCheck.ExecuteScalarAsync(token))!);
        }

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database;
            await database.GetService<IMigrator>().MigrateAsync(cancellationToken: token);
        }

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.group_id IS NULL,
                       to_regclass('catalog.groups') IS NOT NULL,
                       (SELECT confdeltype = 'r' FROM pg_constraint
                        WHERE conname = 'FK_catalog_products_groups'),
                       (SELECT COUNT(*) = 5 FROM pg_class c
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'catalog' AND c.relname IN
                        ('group_creation_commands', 'product_group_change_commands',
                         'product_operational_name_change_commands', 'product_retire_commands',
                         'product_reactivate_commands'))
                FROM catalog.products p WHERE p.id = $1
                """;
            command.Parameters.AddWithValue(product.Id);
            await using var row = await command.ExecuteReaderAsync(token);
            Assert.True(await row.ReadAsync(token));
            Assert.True(row.GetBoolean(0));
            Assert.True(row.GetBoolean(1));
            Assert.True(row.GetBoolean(2));
            Assert.True(row.GetBoolean(3));
            await row.DisposeAsync();

            var firstGroupId = Guid.CreateVersion7();
            await using (var unique = connection.CreateCommand())
            {
                unique.CommandText = "INSERT INTO catalog.groups (id, operational_name) VALUES ($1, 'Bebidas')";
                unique.Parameters.AddWithValue(firstGroupId);
                await unique.ExecuteNonQueryAsync(token);
            }
            await using (var duplicate = connection.CreateCommand())
            {
                duplicate.CommandText = "INSERT INTO catalog.groups (id, operational_name) VALUES ($1, 'bebidas')";
                duplicate.Parameters.AddWithValue(Guid.CreateVersion7());
                await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync(token));
            }

            await using var activeName = connection.CreateCommand();
            activeName.CommandText = "INSERT INTO catalog.products (id, operational_name, price, is_active, is_available, requires_preparation) VALUES ($1, 'legacy', 3, true, true, false)";
            activeName.Parameters.AddWithValue(Guid.CreateVersion7());
            await Assert.ThrowsAsync<PostgresException>(() => activeName.ExecuteNonQueryAsync(token));
        }

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database;
            await database.GetService<IMigrator>().MigrateAsync(previous, cancellationToken: token);
        }

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(token);
            await using var downProduct = connection.CreateCommand();
            downProduct.CommandText = "SELECT COUNT(*) FROM catalog.products WHERE id = $1";
            downProduct.Parameters.AddWithValue(product.Id);
            Assert.Equal(1L, (long)(await downProduct.ExecuteScalarAsync(token))!);
            await using var downGroups = connection.CreateCommand();
            downGroups.CommandText = "SELECT to_regclass('catalog.groups') IS NULL";
            Assert.True((bool)(await downGroups.ExecuteScalarAsync(token))!);
            await using var downColumn = connection.CreateCommand();
            downColumn.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'catalog' AND table_name = 'products' AND column_name = 'group_id'";
            Assert.Equal(0L, (long)(await downColumn.ExecuteScalarAsync(token))!);
        }

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database;
            await database.GetService<IMigrator>().MigrateAsync(cancellationToken: token);
        }
    }

    private async Task<GroupResponse> CreateGroupAsync(string name, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Post, Groups, Guid.NewGuid(), new { operationalName = name }, token);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<GroupResponse>(await response.Content.ReadFromJsonAsync<GroupResponse>(token));
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, Guid key, object? body, CancellationToken token)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return fixture.Client.SendAsync(request, token);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string route, Guid key, object? body,
        CancellationToken token)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return client.SendAsync(request, token);
    }

    private async Task<DurableCommand> PrepareCommandAsync(
        string family, HttpClient client, CancellationToken token)
    {
        var key = Guid.NewGuid();
        return family switch
        {
            "CreateGroup" => new(HttpMethod.Post, Groups, key,
                new { operationalName = "Replay Group" }, HttpStatusCode.Created),
            "ChangeProductGroup" => await PrepareGroupChangeAsync(key, token),
            "RenameProduct" => await PrepareRenameAsync(key, token),
            "RetireProduct" => await PrepareLifecycleAsync(key, false, token),
            "ReactivateProduct" => await PrepareLifecycleAsync(key, true, token),
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
    }

    private async Task<DurableCommand> PrepareChangedCommandAsync(
        string family, DurableCommand original, CancellationToken token)
    {
        return family switch
        {
            "CreateGroup" => original with { Body = new { operationalName = "Changed Group" } },
            "ChangeProductGroup" => original with { Body = new { expectedCurrentGroupId = (Guid?)null, newGroupId = (await CreateGroupAsync("Changed Group", token)).Id } },
            "RenameProduct" => original with { Body = new { expectedCurrentOperationalName = "Original", newOperationalName = "Changed" } },
            "RetireProduct" => original with { Route = $"{Products}/{(await fixture.CreateProductAsync("Other", "2", token)).Id:D}/retire" },
            "ReactivateProduct" => original with { Route = $"{Products}/{(await fixture.CreateProductAsync("Other", "2", token)).Id:D}/reactivate" },
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
    }

    private async Task<DurableCommand> PrepareGroupChangeAsync(Guid key, CancellationToken token)
    {
        var product = await fixture.CreateProductAsync("Group Product", "2", token);
        var group = await CreateGroupAsync("Original Group", token);
        return new(HttpMethod.Post, $"{Products}/{product.Id:D}/group-changes", key,
            new { expectedCurrentGroupId = (Guid?)null, newGroupId = group.Id }, HttpStatusCode.OK);
    }

    private async Task<DurableCommand> PrepareRenameAsync(Guid key, CancellationToken token)
    {
        var product = await fixture.CreateProductAsync("Original", "2", token);
        return new(HttpMethod.Post, $"{Products}/{product.Id:D}/operational-name-changes", key,
            new { expectedCurrentOperationalName = "Original", newOperationalName = "Renamed" }, HttpStatusCode.OK);
    }

    private async Task<DurableCommand> PrepareLifecycleAsync(Guid key, bool reactivate, CancellationToken token)
    {
        var product = await fixture.CreateProductAsync(reactivate ? "Reactivate" : "Retire", "2", token);
        if (reactivate)
        {
            using var retired = await SendAsync(HttpMethod.Post, $"{Products}/{product.Id:D}/retire", Guid.NewGuid(), null, token);
            retired.EnsureSuccessStatusCode();
        }
        return new(HttpMethod.Post, $"{Products}/{product.Id:D}/{(reactivate ? "reactivate" : "retire")}", key,
            null, HttpStatusCode.OK);
    }

    private sealed record DurableCommand(
        HttpMethod Method, string Route, Guid Key, object? Body, HttpStatusCode SuccessStatus);

    private static async Task<JsonDocument> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, CancellationToken token)
    {
        Assert.Equal(status, response.StatusCode);
        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        return document;
    }
}
