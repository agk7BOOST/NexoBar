using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;
using Npgsql;
using Testcontainers.PostgreSql;

namespace NexoBar.Inventory.IntegrationTests;

public sealed class InventoryApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.6")
        .WithDatabase("nexobar_inventory_tests")
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
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .Database.MigrateAsync();
        await scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>()
            .Database.MigrateAsync();
    }

    internal async Task ResetAsync(CancellationToken cancellationToken)
    {
        Client.Dispose();
        Client = application!.CreateClient();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            TRUNCATE TABLE
                inventory.item_creation_commands,
                inventory.inventory_items,
                identities_and_capabilities.sessions,
                identities_and_capabilities.administrative_commands,
                identities_and_capabilities.preparation_enablements,
                identities_and_capabilities.responsibility_assignments,
                identities_and_capabilities.local_credentials,
                identities_and_capabilities.identities
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task<InventoryActor> CreateActorAsync(
        string suffix,
        CancellationToken cancellationToken,
        params FunctionalResponsibility[] responsibilities)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        var identity = new Identity($"Inventory actor {suffix}", true);
        dbContext.Identities.Add(identity);
        dbContext.ResponsibilityAssignments.AddRange(
            responsibilities.Select(responsibility =>
                new ResponsibilityAssignment(identity.Id, responsibility)));
        await dbContext.SaveChangesAsync(cancellationToken);

        var loginIdentifier = $"inventory-{suffix}-{Guid.NewGuid():N}";
        var secret = $"inventory-secret-{Guid.NewGuid():N}";
        await scope.ServiceProvider.GetRequiredService<LocalCredentialProvisioner>()
            .ProvisionAsync(
                identity.Id,
                loginIdentifier,
                secret,
                cancellationToken);
        return new InventoryActor(identity.Id, loginIdentifier, secret);
    }

    internal async Task LoginAsync(
        InventoryActor actor,
        CancellationToken cancellationToken)
    {
        var antiforgery = await GetAntiforgeryTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/identity-sessions")
        {
            Content = JsonContent.Create(new
            {
                loginIdentifier = actor.LoginIdentifier,
                secret = actor.Secret
            })
        };
        request.Headers.Add("X-NexoBar-CSRF", antiforgery);
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    internal async Task<string> GetAntiforgeryTokenAsync(
        CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(
            "/api/security/antiforgery",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("requestToken").GetString()!;
    }

    internal async Task<HttpResponseMessage> PostItemAsync(
        Guid key,
        string? operationalName,
        string? operationalUnit,
        CancellationToken cancellationToken,
        string? antiforgeryToken = null)
    {
        antiforgeryToken ??= await GetAntiforgeryTokenAsync(cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/items")
        {
            Content = JsonContent.Create(new
            {
                operationalName,
                operationalUnit
            })
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        request.Headers.Add("X-NexoBar-CSRF", antiforgeryToken);
        return await Client.SendAsync(request, cancellationToken);
    }

    internal async Task<(int Items, int Commands)> CountInventoryAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        return (
            await dbContext.InventoryItems.CountAsync(cancellationToken),
            await dbContext.InventoryItemCreationCommands.CountAsync(cancellationToken));
    }

    internal async Task<InventoryItem> ReadItemAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<InventoryDbContext>()
            .InventoryItems.AsNoTracking()
            .SingleAsync(item => item.Id == itemId, cancellationToken);
    }

    internal async Task<InventoryItem> AddItemAsync(
        string operationalName,
        string operationalUnit,
        CancellationToken cancellationToken)
    {
        var item = InventoryItem.TryCreate(operationalName, operationalUnit).Item!;
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        dbContext.InventoryItems.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        return item;
    }

    internal async Task DeactivateIdentityAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        var identity = await dbContext.Identities.SingleAsync(
            candidate => candidate.Id == identityId,
            cancellationToken);
        identity.Deactivate();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal async Task RevokeResponsibilityAsync(
        Guid identityId,
        FunctionalResponsibility responsibility,
        CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        var assignment = await dbContext.ResponsibilityAssignments.SingleAsync(
            candidate => candidate.IdentityId == identityId &&
                candidate.ResponsibilityCode == responsibility,
            cancellationToken);
        dbContext.ResponsibilityAssignments.Remove(assignment);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal async Task RevokeSessionsAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE identities_and_capabilities.sessions SET revoked_at = now() WHERE identity_id = {identityId}",
            cancellationToken);
    }

    internal async Task SetRegisteredStateAsync(
        Guid itemId,
        decimal? quantity,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE inventory.inventory_items SET current_registered_quantity = {quantity}, movement_revision = {revision} WHERE id = {itemId}",
            cancellationToken);
    }

    internal async Task SetCommandFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var sql = enabled
            ? """
              CREATE OR REPLACE FUNCTION inventory.fail_item_creation_command()
              RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN
                  RAISE EXCEPTION 'controlled item creation command failure';
              END;
              $$;
              CREATE TRIGGER fail_item_creation_command
              BEFORE INSERT ON inventory.item_creation_commands
              FOR EACH ROW EXECUTE FUNCTION inventory.fail_item_creation_command();
              """
            : """
              DROP TRIGGER IF EXISTS fail_item_creation_command
                  ON inventory.item_creation_commands;
              DROP FUNCTION IF EXISTS inventory.fail_item_creation_command();
              """;
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    internal async Task MigrateInventoryAsync(
        string? targetMigration,
        CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await dbContext.GetInfrastructure().GetRequiredService<IMigrator>()
            .MigrateAsync(targetMigration, cancellationToken);
    }

    internal async Task<bool> HasPendingModelChangesAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<InventoryDbContext>()
            .Database.HasPendingModelChanges();
    }

    internal HttpClient CreateAnonymousClient() => application!.CreateClient();

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
                    "ConnectionStrings:IdentitiesAndCapabilities",
                    connectionString);
                builder.UseSetting("ConnectionStrings:Inventory", connectionString);
                builder.UseSetting(
                    "ConnectionStrings:OperationalConfiguration",
                    connectionString);
                builder.UseSetting("ConnectionStrings:OrderOperations", connectionString);
                builder.UseSetting(
                    "NexoBarSecurity:Cookies:SessionName",
                    "nexobar-inventory-session-test");
                builder.UseSetting(
                    "NexoBarSecurity:Cookies:AntiforgeryName",
                    "nexobar-inventory-antiforgery-test");
                builder.UseSetting("NexoBarSecurity:Cookies:Secure", "false");
            });
        Client = application.CreateClient();
    }
}

internal sealed record InventoryActor(
    Guid IdentityId,
    string LoginIdentifier,
    string Secret);

[CollectionDefinition(Name)]
public sealed class InventoryApiCollection : ICollectionFixture<InventoryApiFixture>
{
    public const string Name = "Inventory API with PostgreSQL";
}
