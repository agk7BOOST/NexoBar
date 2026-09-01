using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.OperationalConfiguration;
using Testcontainers.PostgreSql;

namespace NexoBar.OperationalConfiguration.IntegrationTests;

public sealed class OperationalConfigurationApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.6")
        .WithDatabase("nexobar_operational_configuration_tests")
        .WithUsername("nexobar_tests")
        .WithPassword("nexobar_tests_password")
        .Build();
    private WebApplicationFactory<Program>? application;

    internal HttpClient Client { get; private set; } = null!;
    internal IServiceProvider Services => application!.Services;

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        StartApplication();
        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<OperationalConfigurationDbContext>()
            .Database.MigrateAsync();
    }

    internal async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<OperationalConfigurationDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                operational_configuration.preparation_responsibility_creation_commands,
                operational_configuration.preparation_responsibilities
            """,
            cancellationToken);
    }

    internal async Task<(int Responsibilities, int Commands)> CountAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<OperationalConfigurationDbContext>();
        return (
            await dbContext.PreparationResponsibilities.CountAsync(cancellationToken),
            await dbContext.PreparationResponsibilityCreationCommands
                .CountAsync(cancellationToken));
    }

    internal async Task SetCommandFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<OperationalConfigurationDbContext>();
        var sql = enabled
            ? """
              CREATE OR REPLACE FUNCTION
                  operational_configuration.fail_responsibility_command()
              RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN
                  RAISE EXCEPTION 'controlled responsibility command failure';
              END;
              $$;
              CREATE TRIGGER fail_responsibility_command
              BEFORE INSERT ON
                  operational_configuration.preparation_responsibility_creation_commands
              FOR EACH ROW EXECUTE FUNCTION
                  operational_configuration.fail_responsibility_command();
              """
            : """
              DROP TRIGGER IF EXISTS fail_responsibility_command ON
                  operational_configuration.preparation_responsibility_creation_commands;
              DROP FUNCTION IF EXISTS
                  operational_configuration.fail_responsibility_command();
              """;
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    internal async Task RestartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Client.Dispose();
        await application!.DisposeAsync();
        StartApplication();
    }

    internal async Task<bool> HasPendingModelChangesAsync()
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return scope.ServiceProvider
            .GetRequiredService<OperationalConfigurationDbContext>()
            .Database.HasPendingModelChanges();
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
                var connectionString = postgres.GetConnectionString();
                builder.UseSetting("ConnectionStrings:Catalog", connectionString);
                builder.UseSetting(
                    "ConnectionStrings:IdentitiesAndCapabilities", connectionString);
                builder.UseSetting("ConnectionStrings:Inventory", connectionString);
                builder.UseSetting(
                    "ConnectionStrings:OperationalConfiguration", connectionString);
                builder.UseSetting("ConnectionStrings:OrderOperations", connectionString);
            });
        Client = application.CreateClient();
    }
}

[CollectionDefinition(Name)]
public sealed class OperationalConfigurationApiCollection :
    ICollectionFixture<OperationalConfigurationApiFixture>
{
    public const string Name = "OperationalConfiguration API with PostgreSQL";
}
