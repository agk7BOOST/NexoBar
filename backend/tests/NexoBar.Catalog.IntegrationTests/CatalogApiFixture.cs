using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
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

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        StartApplication();

        await using var scope = application!.Services.CreateAsyncScope();
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
                catalog.product_price_change_commands,
                catalog.product_creation_commands,
                catalog.products
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
        application = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(
                    "ConnectionStrings:Catalog",
                    postgres.GetConnectionString());
                builder.UseSetting(
                    "ConnectionStrings:OrderOperations",
                    postgres.GetConnectionString());
            });

        Client = application.CreateClient();
    }
}

[CollectionDefinition(Name)]
public sealed class CatalogApiCollection : ICollectionFixture<CatalogApiFixture>
{
    public const string Name = "Catalog API with PostgreSQL";
}
