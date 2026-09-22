using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OperationalConfiguration;
using Testcontainers.PostgreSql;

namespace NexoBar.Catalog.IntegrationTests;

public sealed class CatalogApiFixture : IAsyncLifetime
{
    private const string DefaultAdministratorLogin = "catalog-test-administrator";
    private const string DefaultAdministratorSecret = "secret";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.6")
        .WithDatabase("nexobar_catalog_tests")
        .WithUsername("nexobar_tests")
        .WithPassword("nexobar_tests_password")
        .Build();

    private WebApplicationFactory<Program>? application;

    public HttpClient Client { get; private set; } = null!;
    internal string ConnectionString => postgres.GetConnectionString();
    internal IServiceProvider Services => application!.Services;

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        StartApplication();

        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<OperationalConfigurationDbContext>()
            .Database.MigrateAsync();
        await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .Database.MigrateAsync();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await dbContext.Database.MigrateAsync();
        await EnsureDefaultAdministratorAsync();
        await AuthenticateAsync(Client, DefaultAdministratorLogin, DefaultAdministratorSecret);
    }

    public async Task ResetCatalogAsync(CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                catalog.product_reactivate_commands,
                catalog.product_retire_commands,
                catalog.product_availability_change_commands,
                catalog.product_operational_name_change_commands,
                catalog.product_group_change_commands,
                catalog.group_creation_commands,
                catalog.product_preparation_configuration_change_commands,
                catalog.product_price_change_commands,
                catalog.product_creation_commands,
                catalog.products,
                catalog.groups,
                operational_configuration.preparation_responsibility_creation_commands,
                operational_configuration.preparation_responsibilities
            """,
            cancellationToken);
    }

    internal async Task<ProductResponse> CreateProductAsync(
        string name,
        string price,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/products")
        {
            Content = JsonContent.Create(new CreateProductRequest(name, price, false))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));

        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<ProductResponse>(
            await response.Content.ReadFromJsonAsync<ProductResponse>(cancellationToken));
    }

    internal async Task<PreparationResponsibilityResponse>
        CreatePreparationResponsibilityAsync(
            string name,
            CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/operational-configuration/preparation-responsibilities")
        {
            Content = JsonContent.Create(new { operationalName = name })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<PreparationResponsibilityResponse>(
            await response.Content.ReadFromJsonAsync<PreparationResponsibilityResponse>(
                cancellationToken));
    }

    internal async Task<(bool RequiresPreparation, Guid? ResponsibilityId, int Commands)>
        ReadPreparationStateAsync(Guid productId, CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var product = await dbContext.Products.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == productId, cancellationToken);
        return (
            product.RequiresPreparation,
            product.PreparationResponsibilityId,
            await dbContext.ProductPreparationConfigurationChangeCommands
                .CountAsync(cancellationToken));
    }

    internal async Task SetPreparationCommandFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var sql = enabled
            ? """
              CREATE OR REPLACE FUNCTION
                  catalog.fail_product_preparation_configuration_command()
              RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN
                  RAISE EXCEPTION 'controlled preparation configuration command failure';
              END;
              $$;
              CREATE TRIGGER fail_product_preparation_configuration_command
              BEFORE INSERT ON
                  catalog.product_preparation_configuration_change_commands
              FOR EACH ROW EXECUTE FUNCTION
                  catalog.fail_product_preparation_configuration_command();
              """
            : """
              DROP TRIGGER IF EXISTS
                  fail_product_preparation_configuration_command ON
                  catalog.product_preparation_configuration_change_commands;
              DROP FUNCTION IF EXISTS
                  catalog.fail_product_preparation_configuration_command();
              """;
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    internal WebApplicationFactory<Program> CreateApplicationWithPreparationLookup(
        IPreparationResponsibilityLookup replacement) =>
        CreateApplication(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPreparationResponsibilityLookup>();
                services.AddScoped(_ => replacement);
            }));

    internal async Task<bool> WaitForPreparationUpdateLockAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND state = 'active'
                      AND wait_event_type = 'Lock'
                      AND query LIKE 'UPDATE catalog.products%preparation_responsibility_id%')
                """;
            if (Assert.IsType<bool>(await command.ExecuteScalarAsync(cancellationToken)))
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
        return false;
    }

    internal async Task SetProductActiveAsync(
        Guid productId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE catalog.products SET is_active = {isActive} WHERE id = {productId}",
            cancellationToken);
    }

    internal async Task<(decimal Price, int Commands)> ReadPriceChangeStateAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var price = await dbContext.Products
            .Where(product => product.Id == productId)
            .Select(product => product.Price)
            .SingleAsync(cancellationToken);
        var commands = await dbContext.ProductPriceChangeCommands
            .CountAsync(cancellationToken);
        return (price, commands);
    }

    internal async Task SetPriceChangeCommandFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var sql = enabled
            ? """
              CREATE OR REPLACE FUNCTION catalog.fail_product_price_change_command()
              RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN
                  RAISE EXCEPTION 'controlled product price change command failure';
              END;
              $$;
              CREATE TRIGGER fail_product_price_change_command
              BEFORE INSERT ON catalog.product_price_change_commands
              FOR EACH ROW EXECUTE FUNCTION catalog.fail_product_price_change_command();
              """
            : """
              DROP TRIGGER IF EXISTS fail_product_price_change_command
                  ON catalog.product_price_change_commands;
              DROP FUNCTION IF EXISTS catalog.fail_product_price_change_command();
              """;
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    internal async Task<bool> HasPendingModelChangesAsync()
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<CatalogDbContext>()
            .Database.HasPendingModelChanges();
    }

    internal async Task AuthenticateAsync(
        HttpClient client,
        string loginIdentifier,
        string secret)
    {
        var token = await GetAntiforgeryAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity-sessions")
        {
            Content = JsonContent.Create(new { loginIdentifier, secret })
        };
        request.Headers.Add("X-NexoBar-CSRF", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Remove("X-NexoBar-CSRF");
        client.DefaultRequestHeaders.Add(
            "X-NexoBar-CSRF",
            await GetAntiforgeryAsync(client));
    }

    internal async Task<TestCatalogActor> CreateActorAsync(
        string name,
        FunctionalResponsibility[] responsibilities,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        var identity = new Identity(name, true);
        dbContext.Identities.Add(identity);
        foreach (var responsibility in responsibilities)
        {
            dbContext.ResponsibilityAssignments.Add(new ResponsibilityAssignment(
                identity.Id, responsibility));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        var login = $"{name.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()}-{Guid.NewGuid():N}";
        await scope.ServiceProvider.GetRequiredService<LocalCredentialProvisioner>()
            .ProvisionAsync(identity.Id, login, "secret", cancellationToken);
        return new TestCatalogActor(identity.Id, login, "secret");
    }

    internal async Task<HttpClient> CreateAuthenticatedClientAsync(
        TestCatalogActor actor)
    {
        var client = application!.CreateClient();
        await AuthenticateAsync(client, actor.LoginIdentifier, actor.Secret);
        return client;
    }

    internal HttpClient CreateUnauthenticatedClient() => application!.CreateClient();

    internal async Task SetProductAvailableAsync(
        Guid productId,
        bool isAvailable,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database
            .ExecuteSqlInterpolatedAsync(
                $"UPDATE catalog.products SET is_available = {isAvailable} WHERE id = {productId}",
                cancellationToken);
    }

    internal async Task RemoveResponsibilityAsync(
        Guid identityId,
        FunctionalResponsibility responsibility,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .ResponsibilityAssignments
            .Where(assignment => assignment.IdentityId == identityId &&
                assignment.ResponsibilityCode == responsibility)
            .ExecuteDeleteAsync(cancellationToken);
    }

    internal async Task<Guid?> ReadCreationCommandActorAsync(
        Guid key,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>()
            .ProductCreationCommands.AsNoTracking()
            .Where(command => command.IdempotencyKey == key)
            .Select(command => command.ActorIdentityId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    internal async Task InsertLegacyCreationCommandAsync(
        Guid key,
        Guid productId,
        string name,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        dbContext.Products.Add(new Product(productId, name, 1m));
        await dbContext.SaveChangesAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO catalog.product_creation_commands
                (idempotency_key, actor_identity_id, command_kind, intent_operational_name,
                 intent_price, intent_requires_preparation, result_product_id,
                 result_is_active, result_is_available)
            VALUES ({key}, NULL, {"CreateProduct"}, {name}, {1m}, false,
                    {productId}, true, true)
            """,
            cancellationToken);
    }

    internal async Task VerifyActorMigrationRoundTripAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database;
        Assert.True(await HasActorColumnsAsync(database, cancellationToken));
        await database.GetService<IMigrator>().MigrateAsync(
            "20260830130000_AddProductPreparationConfiguration", cancellationToken);
        Assert.False(await HasActorColumnsAsync(database, cancellationToken));
        await database.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
        Assert.True(await HasActorColumnsAsync(database, cancellationToken));
    }

    public async Task RestartApplicationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Client.Dispose();
        await application!.DisposeAsync();
        StartApplication();
        await AuthenticateAsync(Client, DefaultAdministratorLogin, DefaultAdministratorSecret);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        if (application is not null)
        {
            await application.DisposeAsync();
        }

        await postgres.DisposeAsync();
    }

    private void StartApplication()
    {
        application = CreateApplication();

        Client = application.CreateClient();
    }

    private async Task EnsureDefaultAdministratorAsync()
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        if (await dbContext.LocalCredentials.AnyAsync(
                credential => credential.NormalizedLoginIdentifier ==
                    DefaultAdministratorLogin))
        {
            return;
        }

        var identity = new Identity("Catalog Test Administrator", true);
        dbContext.Identities.Add(identity);
        dbContext.ResponsibilityAssignments.Add(new ResponsibilityAssignment(
            identity.Id, FunctionalResponsibility.CatalogConfiguration));
        dbContext.ResponsibilityAssignments.Add(new ResponsibilityAssignment(
            identity.Id, FunctionalResponsibility.GeneralConfiguration));
        await dbContext.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<LocalCredentialProvisioner>()
            .ProvisionAsync(
                identity.Id,
                DefaultAdministratorLogin,
                DefaultAdministratorSecret,
                CancellationToken.None);
    }

    private static async Task<string> GetAntiforgeryAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/security/antiforgery");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("requestToken").GetString()!;
    }

    private static async Task<bool> HasActorColumnsAsync(
        DatabaseFacade database,
        CancellationToken cancellationToken)
    {
        var count = await database.SqlQueryRaw<int>(
                """
                SELECT COUNT(*) AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'catalog'
                  AND column_name = 'actor_identity_id'
                  AND table_name IN (
                    'product_creation_commands',
                    'product_price_change_commands',
                    'product_preparation_configuration_change_commands')
                """)
            .SingleAsync(cancellationToken);
        return count == 3;
    }

    private WebApplicationFactory<Program> CreateApplication(
        Action<IWebHostBuilder>? configure = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(
                    "ConnectionStrings:Catalog",
                    postgres.GetConnectionString());
                builder.UseSetting(
                    "ConnectionStrings:IdentitiesAndCapabilities",
                    postgres.GetConnectionString());
                builder.UseSetting(
                    "ConnectionStrings:Inventory",
                    postgres.GetConnectionString());
                builder.UseSetting(
                    "ConnectionStrings:OperationalConfiguration",
                    postgres.GetConnectionString());
                builder.UseSetting(
                    "ConnectionStrings:OrderOperations",
                    postgres.GetConnectionString());
                configure?.Invoke(builder);
            });
    }
}

internal sealed record TestCatalogActor(
    Guid IdentityId,
    string LoginIdentifier,
    string Secret);

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CatalogApiCollection : ICollectionFixture<CatalogApiFixture>
{
    public const string Name = "Catalog API with PostgreSQL";
}
