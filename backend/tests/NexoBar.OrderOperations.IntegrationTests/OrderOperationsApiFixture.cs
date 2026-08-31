using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexoBar.Catalog;
using NexoBar.OperationalConfiguration;
using NexoBar.OrderOperations;
using Testcontainers.PostgreSql;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed class OrderOperationsApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.6")
        .WithDatabase("nexobar_order_operations_tests")
        .WithUsername("nexobar_tests")
        .WithPassword("nexobar_tests_password")
        .Build();

    private WebApplicationFactory<Program>? application;

    internal HttpClient Client { get; private set; } = null!;
    internal string ConnectionString => postgres.GetConnectionString();
    internal IServiceProvider Services => application!.Services;

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        StartApplication();

        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CatalogDbContext>()
            .Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>()
            .Database.MigrateAsync();
    }

    internal async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                order_operations.subsequent_confirmation_command_contents,
                order_operations.subsequent_confirmation_commands,
                order_operations.first_confirmation_command_contents,
                order_operations.first_confirmation_commands,
                order_operations.preparation_work,
                order_operations.confirmation_history,
                order_operations.incorporation_contents,
                order_operations.incorporations,
                order_operations.orders,
                catalog.product_preparation_configuration_change_commands,
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

    internal async Task SetProductStateAsync(
        Guid productId,
        bool isActive,
        bool isAvailable,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE catalog.products
            SET is_active = {isActive}, is_available = {isAvailable}
            WHERE id = {productId}
            """,
            cancellationToken);
    }

    internal async Task SetProductPreparationAsync(
        Guid productId,
        Guid? preparationResponsibilityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE catalog.products
            SET requires_preparation = {preparationResponsibilityId is not null},
                preparation_responsibility_id = {preparationResponsibilityId}
            WHERE id = {productId}
            """,
            cancellationToken);
    }

    internal async Task SetOrderContextAsync(
        Guid orderId,
        string context,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE order_operations.orders SET context = {context} WHERE id = {orderId}",
            cancellationToken);
    }

    internal async Task<IReadOnlyList<PreparationWorkSnapshot>> ReadPreparationWorkAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        return await (
            from work in dbContext.PreparationWork.AsNoTracking()
            join content in dbContext.IncorporationContents.AsNoTracking()
                on new { work.IncorporationId, work.ContentOrdinal }
                equals new { content.IncorporationId, content.ContentOrdinal }
            orderby work.IncorporationId, work.ContentOrdinal
            select new PreparationWorkSnapshot(
                work.Id,
                work.IncorporationId,
                work.ContentOrdinal,
                content.ProductId,
                content.Instruction,
                work.PreparationResponsibilityId,
                work.TotalQuantity,
                work.PendingQuantity,
                work.InPreparationQuantity,
                work.ReadyQuantity)).ToArrayAsync(cancellationToken);
    }

    internal async Task<int> CountPreparationWorkAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>()
            .PreparationWork.CountAsync(cancellationToken);
    }

    internal async Task<PersistenceCounts> CountEffectsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        return new PersistenceCounts(
            await dbContext.Orders.CountAsync(cancellationToken),
            await dbContext.Incorporations.CountAsync(cancellationToken),
            await dbContext.IncorporationContents.CountAsync(cancellationToken),
            await dbContext.ConfirmationHistory.CountAsync(cancellationToken),
            await dbContext.FirstConfirmationCommands.CountAsync(cancellationToken),
            await dbContext.FirstConfirmationCommandContents.CountAsync(cancellationToken));
    }

    internal async Task<OrderOperationsSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        return new OrderOperationsSnapshot(
            await dbContext.Orders.AsNoTracking().SingleAsync(cancellationToken),
            await dbContext.Incorporations.AsNoTracking().SingleAsync(cancellationToken),
            await dbContext.IncorporationContents.AsNoTracking().ToArrayAsync(cancellationToken),
            await dbContext.ConfirmationHistory.AsNoTracking().SingleAsync(cancellationToken),
            await dbContext.FirstConfirmationCommands.AsNoTracking().SingleAsync(cancellationToken));
    }

    internal async Task SetHistoryFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();

        if (enabled)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION order_operations.fail_confirmation_history()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'controlled confirmation history failure';
                END;
                $$;
                CREATE TRIGGER fail_confirmation_history
                BEFORE INSERT ON order_operations.confirmation_history
                FOR EACH ROW EXECUTE FUNCTION order_operations.fail_confirmation_history();
                """,
                cancellationToken);
        }
        else
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER IF EXISTS fail_confirmation_history
                    ON order_operations.confirmation_history;
                DROP FUNCTION IF EXISTS order_operations.fail_confirmation_history();
                """,
                cancellationToken);
        }
    }

    internal async Task SetPreparationWorkFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();

        var sql = enabled
            ? """
              CREATE OR REPLACE FUNCTION order_operations.fail_preparation_work()
              RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN
                  RAISE EXCEPTION 'controlled preparation work failure';
              END;
              $$;
              CREATE TRIGGER fail_preparation_work
              BEFORE INSERT ON order_operations.preparation_work
              FOR EACH ROW EXECUTE FUNCTION order_operations.fail_preparation_work();
              """
            : """
              DROP TRIGGER IF EXISTS fail_preparation_work
                  ON order_operations.preparation_work;
              DROP FUNCTION IF EXISTS order_operations.fail_preparation_work();
              """;
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    internal async Task SetSubsequentCommandFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();

        if (enabled)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION order_operations.fail_subsequent_command()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'controlled subsequent command failure';
                END;
                $$;
                CREATE TRIGGER fail_subsequent_command
                BEFORE INSERT ON order_operations.subsequent_confirmation_commands
                FOR EACH ROW EXECUTE FUNCTION order_operations.fail_subsequent_command();
                """,
                cancellationToken);
        }
        else
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER IF EXISTS fail_subsequent_command
                    ON order_operations.subsequent_confirmation_commands;
                DROP FUNCTION IF EXISTS order_operations.fail_subsequent_command();
                """,
                cancellationToken);
        }
    }

    internal async Task<SubsequentPersistenceCounts> CountSubsequentEffectsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        return new SubsequentPersistenceCounts(
            await dbContext.Incorporations.CountAsync(cancellationToken),
            await dbContext.IncorporationContents.CountAsync(cancellationToken),
            await dbContext.ConfirmationHistory.CountAsync(cancellationToken),
            await dbContext.SubsequentConfirmationCommands.CountAsync(cancellationToken),
            await dbContext.SubsequentConfirmationCommandContents.CountAsync(cancellationToken));
    }

    internal async Task<bool> WaitForOrderRowLockWaitersAsync(
        int minimumWaiters,
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
                SELECT count(*)
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query LIKE '%FROM order_operations.orders%FOR UPDATE%'
                """;
            var count = Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken));
            if (count >= minimumWaiters)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        return false;
    }

    internal async Task<bool> HasPendingModelChangesAsync()
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>()
            .Database.HasPendingModelChanges();
    }

    internal async Task MigrateOrderOperationsAsync(
        string targetMigration,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        await dbContext.GetService<IMigrator>()
            .MigrateAsync(targetMigration, cancellationToken);
    }

    internal async Task RestartApplicationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Client.Dispose();
        await application!.DisposeAsync();
        StartApplication();
    }

    internal WebApplicationFactory<Program> CreateApplicationWithCatalog(
        IOrderConfirmationCatalog replacement) =>
        CreateApplication(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOrderConfirmationCatalog>();
                services.AddScoped(_ => replacement);
            }));

    internal WebApplicationFactory<Program> CreateApplicationWithCatalogDecorator(
        Func<IServiceProvider, IOrderConfirmationCatalog> factory) =>
        CreateApplication(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOrderConfirmationCatalog>();
                services.AddScoped(factory);
            }));

    internal WebApplicationFactory<Program>
        CreateApplicationWithCatalogDecoratorAndPreparationLookup(
            Func<IServiceProvider, IOrderConfirmationCatalog> catalogFactory,
            IPreparationResponsibilityLookup preparationLookup) =>
        CreateApplication(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOrderConfirmationCatalog>();
                services.AddScoped(catalogFactory);
                services.RemoveAll<IPreparationResponsibilityLookup>();
                services.AddScoped(_ => preparationLookup);
            }));

    internal async Task<bool> WaitForPriceUpdateLockAsync(
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
                      AND query LIKE 'UPDATE catalog.products%')
                """;
            if (Assert.IsType<bool>(await command.ExecuteScalarAsync(cancellationToken)))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        return false;
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
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(
                    "ConnectionStrings:Catalog",
                    postgres.GetConnectionString());
                builder.UseSetting(
                    "ConnectionStrings:IdentitiesAndCapabilities",
                    postgres.GetConnectionString());
                builder.UseSetting(
                    "ConnectionStrings:OperationalConfiguration",
                    postgres.GetConnectionString());
                builder.UseSetting(
                    "ConnectionStrings:OrderOperations",
                    postgres.GetConnectionString());
                configure?.Invoke(builder);
            });

        return factory;
    }
}

internal sealed record PersistenceCounts(
    int Orders,
    int Incorporations,
    int Contents,
    int History,
    int Commands,
    int CommandContents)
{
    internal static readonly PersistenceCounts Empty = new(0, 0, 0, 0, 0, 0);
}

internal sealed record OrderOperationsSnapshot(
    Order Order,
    Incorporation Incorporation,
    IReadOnlyList<IncorporationContent> Contents,
    ConfirmationHistory History,
    FirstConfirmationCommand Command);

internal sealed record SubsequentPersistenceCounts(
    int Incorporations,
    int Contents,
    int History,
    int Commands,
    int CommandContents);

internal sealed record PreparationWorkSnapshot(
    Guid Id,
    Guid IncorporationId,
    int ContentOrdinal,
    Guid ProductId,
    string? Instruction,
    Guid PreparationResponsibilityId,
    int TotalQuantity,
    int PendingQuantity,
    int InPreparationQuantity,
    int ReadyQuantity);

[CollectionDefinition(Name)]
public sealed class OrderOperationsApiCollection :
    ICollectionFixture<OrderOperationsApiFixture>
{
    public const string Name = "OrderOperations API with PostgreSQL";
}
