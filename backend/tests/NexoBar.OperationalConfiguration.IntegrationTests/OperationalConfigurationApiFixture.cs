using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;
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
        await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<NexoBar.Catalog.CatalogDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<NexoBar.OrderOperations.OrderOperationsDbContext>().Database.MigrateAsync();
    }

    internal async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<OperationalConfigurationDbContext>();
        foreach (var schema in new[] { "order_operations", "catalog" })
        {
            // Only the isolated fixture database; preserve each module's migration history.
            var tables = await dbContext.Database.SqlQuery<string>($"SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = {schema} AND tablename <> '__ef_migrations_history'").ToArrayAsync(cancellationToken);
            if (tables.Length > 0)
            {
                var sql = "TRUNCATE TABLE " + string.Join(", ", tables.Select(table => $"\"{schema}\".\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
                await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }
        }
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                operational_configuration.context_lifecycle_commands,
                operational_configuration.preparation_responsibility_lifecycle_commands,
                operational_configuration.context_creation_commands,
                operational_configuration.contexts,
                operational_configuration.preparation_responsibility_creation_commands,
                operational_configuration.preparation_responsibilities;
            TRUNCATE TABLE
                identities_and_capabilities.administrative_commands,
                identities_and_capabilities.preparation_enablements,
                identities_and_capabilities.responsibility_assignments,
                identities_and_capabilities.sessions,
                identities_and_capabilities.local_credentials,
                identities_and_capabilities.installation_provisioning,
                identities_and_capabilities.identities
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

    internal HttpClient CreateClient() => application!.CreateClient();

    internal WebApplicationFactory<Program> DecorateServices(Action<IServiceCollection> configure) =>
        application!.WithWebHostBuilder(builder => builder.ConfigureTestServices(configure));

    internal async Task<TestActor> CreateActorAsync(
        string operationalName,
        bool active,
        FunctionalResponsibility? responsibility,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var identities = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        var identity = new Identity(operationalName, active);
        identities.Identities.Add(identity);
        if (responsibility is { } value)
        {
            identities.ResponsibilityAssignments.Add(
                new ResponsibilityAssignment(identity.Id, value));
        }
        await identities.SaveChangesAsync(cancellationToken);
        var login = $"{operationalName.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()}-{Guid.NewGuid():N}";
        await scope.ServiceProvider.GetRequiredService<LocalCredentialProvisioner>()
            .ProvisionAsync(identity.Id, login, "secret", cancellationToken);
        return new TestActor(identity.Id, login, "secret");
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

    internal async Task DeactivateAsync(Guid identityId, CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        var identity = await dbContext.Identities.SingleAsync(
            candidate => candidate.Id == identityId,
            cancellationToken);
        identity.Deactivate();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal async Task RevokeSessionsAsync(Guid identityId, CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .Sessions.Where(session => session.IdentityId == identityId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    session => session.RevokedAt,
                    DateTimeOffset.UtcNow),
                cancellationToken);
    }

    internal async Task<Guid?> ReadCommandActorAsync(
        Guid key,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<OperationalConfigurationDbContext>()
            .PreparationResponsibilityCreationCommands.AsNoTracking()
            .Where(command => command.IdempotencyKey == key)
            .Select(command => command.ActorIdentityId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    internal async Task VerifyActorMigrationRoundTripAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var database = scope.ServiceProvider
            .GetRequiredService<OperationalConfigurationDbContext>().Database;
        Assert.True(await HasActorColumnAsync(database, cancellationToken));
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260830120000_InitialOperationalConfiguration", cancellationToken);
        Assert.False(await HasActorColumnAsync(database, cancellationToken));
        await migrator.MigrateAsync(cancellationToken: cancellationToken);
        Assert.True(await HasActorColumnAsync(database, cancellationToken));
    }

    internal async Task InsertLegacyCommandAsync(
        Guid key,
        Guid responsibilityId,
        string name,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<OperationalConfigurationDbContext>();
        dbContext.PreparationResponsibilities.Add(
            new PreparationResponsibility(responsibilityId, name));
        await dbContext.SaveChangesAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO operational_configuration.preparation_responsibility_creation_commands
                (idempotency_key, actor_identity_id, command_kind, intent_operational_name,
                 result_responsibility_id, result_operational_name)
            VALUES ({key}, NULL, {"Create"}, {name}, {responsibilityId}, {name})
            """,
            cancellationToken);
    }

    private static async Task<bool> HasActorColumnAsync(
        DatabaseFacade database,
        CancellationToken cancellationToken)
    {
        var count = await database.SqlQueryRaw<int>(
                """
                SELECT COUNT(*) AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'operational_configuration'
                  AND table_name = 'preparation_responsibility_creation_commands'
                  AND column_name = 'actor_identity_id'
                """)
            .SingleAsync(cancellationToken);
        return count == 1;
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

internal sealed record TestActor(Guid IdentityId, string LoginIdentifier, string Secret);

[CollectionDefinition(Name)]
public sealed class OperationalConfigurationApiCollection :
    ICollectionFixture<OperationalConfigurationApiFixture>
{
    public const string Name = "OperationalConfiguration API with PostgreSQL";
}
