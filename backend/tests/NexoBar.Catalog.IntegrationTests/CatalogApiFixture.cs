using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexoBar.Catalog;
using NexoBar.OperationalConfiguration;
using Testcontainers.PostgreSql;

namespace NexoBar.Catalog.IntegrationTests;

public sealed class CatalogApiFixture : IAsyncLifetime
{
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
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public async Task ResetCatalogAsync(CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                catalog.product_preparation_configuration_change_commands,
                catalog.product_price_change_commands,
                catalog.product_creation_commands,
                catalog.products,
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

    public async Task RestartApplicationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Client.Dispose();
        await application!.DisposeAsync();
        StartApplication();
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

[CollectionDefinition(Name)]
public sealed class CatalogApiCollection : ICollectionFixture<CatalogApiFixture>
{
    public const string Name = "Catalog API with PostgreSQL";
}
