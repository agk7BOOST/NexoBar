using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.OperationalConfiguration;
using Npgsql;

namespace NexoBar.Catalog.IntegrationTests;

[Collection(CatalogApiCollection.Name)]
public sealed class ProductPreparationConfigurationApiTests(CatalogApiFixture fixture)
{
    [Fact]
    public async Task Product_starts_without_preparation_and_can_enable_reassign_disable()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Milanesa", "10", token);
        var responsibilityA = await fixture.CreatePreparationResponsibilityAsync(
            "Cocina", token);
        var responsibilityB = await fixture.CreatePreparationResponsibilityAsync(
            "Barra", token);
        Assert.False(product.RequiresPreparation);
        Assert.Null(product.PreparationResponsibilityId);

        using var enabled = await ChangeAsync(
            fixture.Client, product.Id, null, responsibilityA.Id, Guid.NewGuid(), token);
        var enabledResult = await ReadSuccessAsync(enabled, token);
        Assert.True(enabledResult.RequiresPreparation);
        Assert.Equal(responsibilityA.Id, enabledResult.PreparationResponsibilityId);

        using var reassigned = await ChangeAsync(
            fixture.Client,
            product.Id,
            responsibilityA.Id,
            responsibilityB.Id,
            Guid.NewGuid(),
            token);
        var reassignedResult = await ReadSuccessAsync(reassigned, token);
        Assert.True(reassignedResult.RequiresPreparation);
        Assert.Equal(responsibilityB.Id, reassignedResult.PreparationResponsibilityId);

        using var disabled = await ChangeAsync(
            fixture.Client,
            product.Id,
            responsibilityB.Id,
            null,
            Guid.NewGuid(),
            token);
        var disabledResult = await ReadSuccessAsync(disabled, token);
        Assert.False(disabledResult.RequiresPreparation);
        Assert.Null(disabledResult.PreparationResponsibilityId);
    }

    [Fact]
    public async Task Null_to_null_is_a_valid_durable_success()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "2", token);
        using var response = await ChangeAsync(
            fixture.Client, product.Id, null, null, Guid.NewGuid(), token);
        var result = await ReadSuccessAsync(response, token);
        Assert.False(result.RequiresPreparation);
        Assert.Null(result.PreparationResponsibilityId);
        Assert.Equal((false, null, 1),
            await fixture.ReadPreparationStateAsync(product.Id, token));
    }

    [Fact]
    public async Task Missing_responsibility_is_rejected_without_effect_or_command()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        var missing = Guid.CreateVersion7();
        using var response = await ChangeAsync(
            fixture.Client, product.Id, null, missing, Guid.NewGuid(), token);
        await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "catalog.product.preparation_responsibility_not_found",
            token);
        Assert.Equal((false, null, 0),
            await fixture.ReadPreparationStateAsync(product.Id, token));
    }

    [Fact]
    public async Task Stale_expectation_reports_current_nullable_configuration()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        var responsibility = await fixture.CreatePreparationResponsibilityAsync(
            "Cocina", token);
        using var changed = await ChangeAsync(
            fixture.Client, product.Id, null, responsibility.Id, Guid.NewGuid(), token);
        changed.EnsureSuccessStatusCode();

        using var stale = await ChangeAsync(
            fixture.Client, product.Id, null, null, Guid.NewGuid(), token);
        using var problem = await AssertProblemAsync(
            stale,
            HttpStatusCode.Conflict,
            "catalog.product.preparation_configuration_concurrency_conflict",
            token);
        Assert.Equal(
            responsibility.Id,
            problem.RootElement.GetProperty("currentPreparationResponsibilityId")
                .GetGuid());
    }

    [Fact]
    public async Task Stale_expectation_reports_explicit_null_current_configuration()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "2", token);
        var responsibility = await fixture.CreatePreparationResponsibilityAsync(
            "Barra", token);
        using var stale = await ChangeAsync(
            fixture.Client,
            product.Id,
            responsibility.Id,
            null,
            Guid.NewGuid(),
            token);
        using var problem = await AssertProblemAsync(
            stale,
            HttpStatusCode.Conflict,
            "catalog.product.preparation_configuration_concurrency_conflict",
            token);
        Assert.Equal(
            JsonValueKind.Null,
            problem.RootElement.GetProperty("currentPreparationResponsibilityId").ValueKind);
    }

    [Fact]
    public async Task Same_key_and_same_nullable_intent_replays_one_effect()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        var responsibility = await fixture.CreatePreparationResponsibilityAsync(
            "Cocina", token);
        var key = Guid.NewGuid();
        using var first = await ChangeAsync(
            fixture.Client, product.Id, null, responsibility.Id, key, token);
        var firstResult = await ReadSuccessAsync(first, token);
        using var replay = await ChangeAsync(
            fixture.Client, product.Id, null, responsibility.Id, key, token);
        Assert.Equal(firstResult, await ReadSuccessAsync(replay, token));
        Assert.Equal((true, responsibility.Id, 1),
            await fixture.ReadPreparationStateAsync(product.Id, token));
    }

    [Fact]
    public async Task Same_key_and_different_nullable_intent_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        var responsibility = await fixture.CreatePreparationResponsibilityAsync(
            "Cocina", token);
        var key = Guid.NewGuid();
        using var first = await ChangeAsync(
            fixture.Client, product.Id, null, responsibility.Id, key, token);
        first.EnsureSuccessStatusCode();
        using var conflict = await ChangeAsync(
            fixture.Client, product.Id, responsibility.Id, null, key, token);
        await AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            "catalog.product.preparation_configuration.idempotency_key_conflict",
            token);
    }

    [Fact]
    public async Task Concurrent_same_key_replays_one_effect()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        var responsibility = await fixture.CreatePreparationResponsibilityAsync(
            "Cocina", token);
        var key = Guid.NewGuid();
        var responses = await Task.WhenAll(
            ChangeAsync(fixture.Client, product.Id, null, responsibility.Id, key, token),
            ChangeAsync(fixture.Client, product.Id, null, responsibility.Id, key, token));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal((true, responsibility.Id, 1),
                await fixture.ReadPreparationStateAsync(product.Id, token));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Two_keys_with_same_expected_configuration_allow_one_update()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        var a = await fixture.CreatePreparationResponsibilityAsync("Cocina", token);
        var b = await fixture.CreatePreparationResponsibilityAsync("Barra", token);
        var responses = await Task.WhenAll(
            ChangeAsync(fixture.Client, product.Id, null, a.Id, Guid.NewGuid(), token),
            ChangeAsync(fixture.Client, product.Id, null, b.Id, Guid.NewGuid(), token));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            var state = await fixture.ReadPreparationStateAsync(product.Id, token);
            Assert.True(state.RequiresPreparation);
            Assert.Contains(state.ResponsibilityId, new Guid?[] { a.Id, b.Id });
            Assert.Equal(1, state.Commands);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Replay_survives_restart_and_does_not_consult_responsibility_lookup()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        var responsibility = await fixture.CreatePreparationResponsibilityAsync(
            "Cocina", token);
        var key = Guid.NewGuid();
        using var first = await ChangeAsync(
            fixture.Client, product.Id, null, responsibility.Id, key, token);
        var expected = await ReadSuccessAsync(first, token);
        await fixture.RestartApplicationAsync(token);

        var unexpected = new UnexpectedPreparationResponsibilityLookup();
        await using var application = fixture.CreateApplicationWithPreparationLookup(unexpected);
        using var client = application.CreateClient();
        using var replay = await ChangeAsync(
            client, product.Id, null, responsibility.Id, key, token);
        Assert.Equal(expected, await ReadSuccessAsync(replay, token));
        Assert.False(unexpected.WasCalled);
    }

    [Fact]
    public async Task Command_failure_rolls_back_product_configuration()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        var responsibility = await fixture.CreatePreparationResponsibilityAsync(
            "Cocina", token);
        var key = Guid.NewGuid();
        await fixture.SetPreparationCommandFailureAsync(true, token);
        try
        {
            using var failed = await ChangeAsync(
                fixture.Client, product.Id, null, responsibility.Id, key, token);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal((false, null, 0),
                await fixture.ReadPreparationStateAsync(product.Id, token));
        }
        finally
        {
            await fixture.SetPreparationCommandFailureAsync(false, token);
        }
        using var retry = await ChangeAsync(
            fixture.Client, product.Id, null, responsibility.Id, key, token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task Database_enforces_coherent_state_and_has_no_cross_module_fk()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        var responsibility = await fixture.CreatePreparationResponsibilityAsync(
            "Cocina", token);

        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE catalog.products
                SET requires_preparation = false,
                    preparation_responsibility_id = @responsibility
                WHERE id = @product
                """;
            command.Parameters.AddWithValue("responsibility", responsibility.Id);
            command.Parameters.AddWithValue("product", product.Id);
            await command.ExecuteNonQueryAsync(token);
        });

        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE catalog.products
                SET requires_preparation = true,
                    preparation_responsibility_id = NULL
                WHERE id = @product
                """;
            command.Parameters.AddWithValue("product", product.Id);
            await command.ExecuteNonQueryAsync(token);
        });

        await using var checkConnection = new NpgsqlConnection(fixture.ConnectionString);
        await checkConnection.OpenAsync(token);
        await using var check = checkConnection.CreateCommand();
        check.CommandText =
            """
            SELECT count(*)
            FROM pg_constraint
            WHERE conrelid = 'catalog.products'::regclass
              AND confrelid =
                  'operational_configuration.preparation_responsibilities'::regclass
            """;
        Assert.Equal(0L, Assert.IsType<long>(await check.ExecuteScalarAsync(token)));
    }

    [Fact]
    public async Task Both_nullable_properties_are_required_and_unknown_properties_rejected()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        foreach (var body in new[]
        {
            """{"newPreparationResponsibilityId":null}""",
            """{"expectedCurrentPreparationResponsibilityId":null}""",
            """{"expectedCurrentPreparationResponsibilityId":null,"newPreparationResponsibilityId":null,"status":"ready"}"""
        })
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                Route(product.Id))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            using var response = await fixture.Client.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal((false, null, 0),
            await fixture.ReadPreparationStateAsync(product.Id, token));
    }

    [Fact]
    public async Task OpenApi_describes_nullable_required_configuration_contract()
    {
        var token = TestContext.Current.CancellationToken;
        using var response = await fixture.Client.GetAsync("/openapi/v1.json", token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(token),
            cancellationToken: token);
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty(
                "/api/catalog/products/{productId}/preparation-configuration-changes")
            .GetProperty("post");
        var header = operation.GetProperty("parameters").EnumerateArray()
            .Single(parameter =>
                parameter.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.True(header.GetProperty("required").GetBoolean());
        foreach (var status in new[] { "200", "400", "404", "409" })
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        }
        var schema = document.RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(nameof(ChangeProductPreparationConfigurationRequest));
        var required = schema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()).ToArray();
        Assert.Contains("expectedCurrentPreparationResponsibilityId", required);
        Assert.Contains("newPreparationResponsibilityId", required);
    }

    [Fact]
    public async Task For_share_stabilizes_preparation_configuration_against_update()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        var a = await fixture.CreatePreparationResponsibilityAsync("Cocina", token);
        var b = await fixture.CreatePreparationResponsibilityAsync("Barra", token);
        using var initial = await ChangeAsync(
            fixture.Client, product.Id, null, a.Id, Guid.NewGuid(), token);
        initial.EnsureSuccessStatusCode();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IOrderConfirmationCatalog>();
        var snapshot = await catalog.ReadProductsAsync(
            new[] { product.Id }, transaction, token);

        var updateTask = ChangeAsync(
            fixture.Client, product.Id, a.Id, b.Id, Guid.NewGuid(), token);
        Assert.True(await fixture.WaitForPreparationUpdateLockAsync(
            TimeSpan.FromSeconds(10), token));
        var stabilized = Assert.Single(snapshot);
        Assert.True(stabilized.RequiresPreparation);
        Assert.Equal(a.Id, stabilized.PreparationResponsibilityId);

        await transaction.CommitAsync(token);
        using var updated = await updateTask;
        var result = await ReadSuccessAsync(updated, token);
        Assert.Equal(b.Id, result.PreparationResponsibilityId);
    }

    [Fact]
    public async Task Catalog_model_has_no_pending_changes()
    {
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    private static string Route(Guid productId) =>
        $"/api/catalog/products/{productId:D}/preparation-configuration-changes";

    private static async Task<HttpResponseMessage> ChangeAsync(
        HttpClient client,
        Guid productId,
        Guid? expected,
        Guid? next,
        Guid key,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route(productId))
        {
            Content = JsonContent.Create(new
            {
                expectedCurrentPreparationResponsibilityId = expected,
                newPreparationResponsibilityId = next
            })
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<ProductPreparationConfigurationResponse> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<ProductPreparationConfigurationResponse>(
            await response.Content
                .ReadFromJsonAsync<ProductPreparationConfigurationResponse>(
                    cancellationToken));
    }

    private static async Task<JsonDocument> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        return document;
    }
}

internal sealed class UnexpectedPreparationResponsibilityLookup :
    IPreparationResponsibilityLookup
{
    internal bool WasCalled { get; private set; }

    public Task<bool> ExistsAsync(
        Guid responsibilityId,
        CancellationToken cancellationToken)
    {
        WasCalled = true;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<PreparationResponsibilityReference>> ReadByIdsAsync(
        IReadOnlyCollection<Guid> responsibilityIds,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Preparation Responsibility batch lookup was not expected.");
}
