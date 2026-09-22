using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ConfiguredContextUpgradeMigrationTests(
    OrderOperationsApiFixture fixture)
{
    private const string PreI1aOperationalConfiguration =
        "20260919130000_AddPreparationResponsibilityCreationCommandActor";
    private const string PreI1aOrderOperations =
        "20260921120000_AddUnavailableProductException";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Production_runner_imports_legacy_contexts_backfills_ids_and_preserves_exact_replay()
    {
        await fixture.ResetAsync(Token);
        var product = await fixture.CreateProductAsync("Legacy replay product", "12.50", Token);
        var actor = fixture.DefaultOrderOperationsActor;
        var orders = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var incorporations = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var histories = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var keys = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        try
        {
            await fixture.MigrateOrderOperationsAsync(PreI1aOrderOperations, Token);
            await fixture.MigrateOperationalConfigurationAsync(
                PreI1aOperationalConfiguration,
                Token);
            await SeedLegacyCommittedOrdersAsync(
                actor.IdentityId,
                product.Id,
                orders,
                incorporations,
                histories,
                keys);

            var runner = await RunProductionRunnerAsync();
            Assert.True(runner.ExitCode == 0, runner.Output);

            Guid mesaId;
            Guid barraId;
            await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
            {
                await connection.OpenAsync(Token);
                mesaId = await ReadContextIdAsync(connection, "Mesa 1");
                barraId = await ReadContextIdAsync(connection, "Barra");
                Assert.NotEqual(mesaId, barraId);
                Assert.Equal('7', mesaId.ToString("N")[12]);
                Assert.Equal('7', barraId.ToString("N")[12]);

                await using var check = new NpgsqlCommand(
                    """
                    SELECT
                      (SELECT count(*) FROM operational_configuration.contexts WHERE normalized_operational_name IN ('mesa 1','barra')),
                      (SELECT count(DISTINCT current_context_id) FROM order_operations.orders WHERE id = ANY(@mesa_orders)),
                      (SELECT count(*) FROM order_operations.orders WHERE context IN ('Mesa 1','Barra') AND current_context_id IS NULL),
                      (SELECT count(*) FROM information_schema.referential_constraints rc
                       JOIN information_schema.constraint_column_usage ccu
                         ON ccu.constraint_catalog = rc.unique_constraint_catalog
                        AND ccu.constraint_schema = rc.unique_constraint_schema
                        AND ccu.constraint_name = rc.unique_constraint_name
                       WHERE rc.constraint_schema = 'order_operations'
                         AND ccu.table_schema = 'operational_configuration')
                    """,
                    connection);
                check.Parameters.AddWithValue("mesa_orders", new[] { orders[0], orders[1] });
                // The third scalar intentionally tests the old text snapshots after the ID backfill.
                var result = await check.ExecuteReaderAsync(Token);
                Assert.True(await result.ReadAsync(Token));
                Assert.Equal(2L, result.GetInt64(0));
                Assert.Equal(1L, result.GetInt64(1));
                Assert.Equal(0L, result.GetInt64(2));
                Assert.Equal(0L, result.GetInt64(3));
                await result.DisposeAsync();

                await using var mappings = new NpgsqlCommand(
                    "SELECT id, context, current_context_id FROM order_operations.orders ORDER BY id",
                    connection);
                await using var reader = await mappings.ExecuteReaderAsync(Token);
                var mappingsByText = new Dictionary<string, HashSet<Guid>>();
                while (await reader.ReadAsync(Token))
                {
                    var text = reader.GetString(1);
                    if (!mappingsByText.TryGetValue(text, out var ids))
                    {
                        ids = [];
                        mappingsByText.Add(text, ids);
                    }
                    ids.Add(reader.GetGuid(2));
                }
                Assert.Single(mappingsByText["Mesa 1"]);
                Assert.Contains(mesaId, mappingsByText["Mesa 1"]);
                Assert.Contains(barraId, mappingsByText["Barra"]);
            }

            Assert.Equal("Mesa 1", await ReadHistoryContextAsync(incorporations[0]));
            Assert.Equal("Barra", await ReadHistoryContextAsync(incorporations[2]));
            Assert.Equal(mesaId, await ReadHistoryContextIdAsync(incorporations[0]));
            Assert.Equal(mesaId, await ReadIntentContextIdAsync(keys[0]));
            Assert.Equal("Mesa 1", await ReadIntentContextNameAsync(keys[0]));

            using var replayClient = await fixture.LoginWithoutLegacyRequestAdapterAsync(
                actor,
                Token);
            using var replayRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/order-operations/first-confirmations")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        context = "Mesa 1",
                        items = new[] { new { productId = product.Id, quantity = 1 } }
                    }),
                    Encoding.UTF8,
                    "application/json")
            };
            replayRequest.Headers.Add("Idempotency-Key", keys[0].ToString("D"));
            using var replay = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
                replayClient,
                replayRequest,
                Token);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            var response = Assert.IsType<FirstConfirmationResponse>(
                await replay.Content.ReadFromJsonAsync<FirstConfirmationResponse>(Token));
            Assert.Equal(orders[0].ToString("D"), response.OperationalReference);
            Assert.Equal(mesaId, response.ContextId);
            Assert.Equal("Mesa 1", response.Context);

            using var freshLegacyRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/order-operations/first-confirmations")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        context = "Mesa 1",
                        items = new[] { new { productId = product.Id, quantity = 1 } }
                    }),
                    Encoding.UTF8,
                    "application/json")
            };
            freshLegacyRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            using var rejected = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
                replayClient,
                freshLegacyRequest,
                Token);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(
                "order_operations.first_confirmation.context_id_required",
                await ReadProblemCodeAsync(rejected));

            Assert.Equal((3, 3), await CountOrdersAndIncorporationsAsync());

            await fixture.MigrateOrderOperationsAsync(PreI1aOrderOperations, Token);
            await fixture.MigrateOperationalConfigurationAsync(
                PreI1aOperationalConfiguration,
                Token);
            Assert.Equal((3, 3), await CountOrdersAndIncorporationsAsync());
        }
        finally
        {
            var rerun = await RunProductionRunnerAsync();
            Assert.True(rerun.ExitCode == 0, rerun.Output);
            await fixture.ResetAsync(Token);
        }
    }

    private async Task SeedLegacyCommittedOrdersAsync(
        Guid actorId,
        Guid productId,
        Guid[] orderIds,
        Guid[] incorporationIds,
        Guid[] historyIds,
        Guid[] keys)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Token);
        var contexts = new[] { "Mesa 1", "Mesa 1", "Barra" };
        for (var index = 0; index < contexts.Length; index++)
        {
            await using var transaction = await connection.BeginTransactionAsync(Token);
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO order_operations.orders (id, context) VALUES (@id, @context)",
                ("id", orderIds[index]), ("context", contexts[index]));
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO order_operations.incorporations (id, order_id, ordinal) VALUES (@id, @order, 1)",
                ("id", incorporationIds[index]), ("order", orderIds[index]));
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO order_operations.confirmation_history (id, incorporation_id, confirmed_context, occurred_at, actor_identity_id) VALUES (@id, @inc, @context, @at, @actor)",
                ("id", historyIds[index]), ("inc", incorporationIds[index]),
                ("context", contexts[index]), ("at", DateTimeOffset.UtcNow), ("actor", actorId));
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO order_operations.first_confirmation_commands (idempotency_key, actor_identity_id, intent_context, result_incorporation_id) VALUES (@key, @actor, @context, @inc)",
                ("key", keys[index]), ("actor", actorId), ("context", contexts[index]),
                ("inc", incorporationIds[index]));
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO order_operations.first_confirmation_command_contents (idempotency_key, line_ordinal, product_id, quantity, instruction, intent_unavailable_product_exception_requested) VALUES (@key, 1, @product, 1, NULL, false)",
                ("key", keys[index]), ("product", productId));
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO order_operations.incorporation_contents (incorporation_id, content_ordinal, product_id, quantity, applied_price, instruction, requires_preparation_at_confirmation, unavailable_product_exception_applied) VALUES (@inc, 1, @product, 1, 12.50, NULL, false, false)",
                ("inc", incorporationIds[index]), ("product", productId));
            await transaction.CommitAsync(Token);
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<(int Orders, int Incorporations)> CountOrdersAndIncorporationsAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        return (
            await db.Orders.CountAsync(Token),
            await db.Incorporations.CountAsync(Token));
    }

    private static async Task<Guid> ReadContextIdAsync(NpgsqlConnection connection, string name)
    {
        await using var command = new NpgsqlCommand(
            "SELECT id FROM operational_configuration.contexts WHERE normalized_operational_name = lower(@name)",
            connection);
        command.Parameters.AddWithValue("name", name);
        return (Guid)(await command.ExecuteScalarAsync(Token))!;
    }

    private async Task<string> ReadHistoryContextAsync(Guid incorporationId) =>
        await ReadStringAsync(
            "SELECT confirmed_context FROM order_operations.confirmation_history WHERE incorporation_id = @id",
            incorporationId);

    private async Task<Guid> ReadHistoryContextIdAsync(Guid incorporationId) =>
        await ReadGuidAsync(
            "SELECT confirmed_context_id FROM order_operations.confirmation_history WHERE incorporation_id = @id",
            incorporationId);

    private async Task<Guid> ReadIntentContextIdAsync(Guid key) =>
        await ReadGuidAsync(
            "SELECT intent_context_id FROM order_operations.first_confirmation_commands WHERE idempotency_key = @id",
            key);

    private async Task<string> ReadIntentContextNameAsync(Guid key) =>
        await ReadStringAsync(
            "SELECT intent_context FROM order_operations.first_confirmation_commands WHERE idempotency_key = @id",
            key);

    private async Task<Guid> ReadGuidAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (Guid)(await command.ExecuteScalarAsync(Token))!;
    }

    private async Task<string> ReadStringAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(Token),
            cancellationToken: Token);
        return document.RootElement.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    private async Task<(int ExitCode, string Output)> RunProductionRunnerAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "backend", "NexoBar.slnx")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        var project = Path.Combine(root!.FullName, "backend", "src", "NexoBar.Migrations", "NexoBar.Migrations.csproj");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        foreach (var module in new[]
                 {
                     "OperationalConfiguration", "IdentitiesAndCapabilities", "Catalog", "Inventory", "OrderOperations"
                 })
        {
            start.Environment[$"ConnectionStrings__{module}"] = fixture.ConnectionString;
        }
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        return (process.ExitCode, await stdout + await stderr);
    }
}
