using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OperationalConfiguration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

public sealed class IdentitiesAndCapabilitiesFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.6")
        .WithDatabase("nexobar_identities_and_capabilities_tests")
        .WithUsername("nexobar_tests")
        .WithPassword("nexobar_tests_password")
        .Build();

    private WebApplicationFactory<Program>? application;

    internal ManualTimeProvider Clock { get; } = new();

    internal HttpClient Client { get; private set; } = null!;

    internal string ConnectionString => postgres.GetConnectionString();

    internal HttpClient CreateClient() => application!.CreateClient();

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
    }

    internal async Task ResetAsync(CancellationToken cancellationToken)
    {
        Clock.Reset();
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                identities_and_capabilities.administrative_commands,
                identities_and_capabilities.sessions,
                identities_and_capabilities.local_credentials,
                identities_and_capabilities.preparation_enablements,
                identities_and_capabilities.responsibility_assignments,
                identities_and_capabilities.identities,
                operational_configuration.preparation_responsibility_creation_commands,
                operational_configuration.preparation_responsibilities
            """,
            cancellationToken);
    }

    internal async Task<CredentialSnapshot> ProvisionCredentialAsync(
        Guid identityId,
        string loginIdentifier,
        string secret,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var credential = await scope.ServiceProvider
            .GetRequiredService<LocalCredentialProvisioner>()
            .ProvisionAsync(identityId, loginIdentifier, secret, cancellationToken);
        return new CredentialSnapshot(
            credential.IdentityId,
            credential.LoginIdentifier,
            credential.NormalizedLoginIdentifier,
            credential.SecretVerifier);
    }

    internal async Task<SessionSnapshot[]> ReadSessionsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .Sessions.AsNoTracking()
            .OrderBy(session => session.CreatedAt)
            .ThenBy(session => session.Id)
            .Select(session => new SessionSnapshot(
                session.Id,
                session.IdentityId,
                session.TokenHash,
                session.CreatedAt,
                session.LastActivityAt,
                session.AbsoluteExpiresAt,
                session.RevokedAt))
            .ToArrayAsync(cancellationToken);
    }

    internal async Task<string> ReplaceCredentialVerifierAsync(
        Guid identityId,
        string verifier,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        var credential = await dbContext.LocalCredentials.SingleAsync(
            candidate => candidate.IdentityId == identityId,
            cancellationToken);
        credential.ReplaceSecretVerifier(verifier);
        await dbContext.SaveChangesAsync(cancellationToken);
        return credential.SecretVerifier;
    }

    internal async Task<string> ReadCredentialVerifierAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .LocalCredentials.AsNoTracking()
            .Where(candidate => candidate.IdentityId == identityId)
            .Select(candidate => candidate.SecretVerifier)
            .SingleAsync(cancellationToken);
    }

    internal async Task SetIdentityActiveAsync(
        Guid identityId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .Identities.Where(identity => identity.Id == identityId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(identity => identity.IsActive, isActive),
                cancellationToken);
    }

    internal async Task InsertSessionAsync(
        IdentitySession session,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal async Task<StabilizedAuthenticatedSession?> StabilizeAsync(
        Guid identityId,
        Guid sessionId,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [
                        new System.Security.Claims.Claim(
                            System.Security.Claims.ClaimTypes.NameIdentifier,
                            identityId.ToString("D")),
                        new System.Security.Claims.Claim(
                            SessionAuthenticationDefaults.SessionIdClaim,
                            sessionId.ToString("D"))
                    ],
                    SessionAuthenticationDefaults.Scheme))
        };
        return await scope.ServiceProvider
            .GetRequiredService<IAuthenticatedSessionStabilizer>()
            .StabilizeAsync(transaction, cancellationToken);
    }

    internal async Task<IdentitySnapshot> CreateIdentityAsync(
        string operationalName,
        bool isActive,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        var identity = new Identity(operationalName, isActive);
        dbContext.Identities.Add(identity);
        await dbContext.SaveChangesAsync(cancellationToken);
        await dbContext.Entry(identity).ReloadAsync(cancellationToken);
        return Map(identity);
    }

    internal async Task InsertAssignmentAsync(
        Guid identityId,
        FunctionalResponsibility responsibility,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        dbContext.ResponsibilityAssignments.Add(
            new ResponsibilityAssignment(identityId, responsibility));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal async Task InsertEnablementAsync(
        Guid identityId,
        Guid preparationResponsibilityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        dbContext.PreparationEnablements.Add(
            new PreparationEnablement(identityId, preparationResponsibilityId));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal async Task<GrantPreparationEnablementOutcome> GrantEnablementAsync(
        Guid identityId,
        Guid preparationResponsibilityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<PreparationEnablementService>()
            .GrantAsync(identityId, preparationResponsibilityId, cancellationToken);
    }

    internal async Task<bool> RevokeEnablementAsync(
        Guid identityId,
        Guid preparationResponsibilityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<PreparationEnablementService>()
            .RevokeAsync(identityId, preparationResponsibilityId, cancellationToken);
    }

    internal async Task<Guid> CreatePreparationResponsibilityAsync(
        string operationalName,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/operational-configuration/preparation-responsibilities")
        {
            Content = JsonContent.Create(new { operationalName })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    internal async Task<IdentitySnapshot[]> ReadIdentitiesAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .Identities.AsNoTracking()
            .OrderBy(identity => identity.Id)
            .Select(identity => new IdentitySnapshot(
                identity.Id,
                identity.OperationalName,
                identity.NormalizedOperationalName,
                identity.IsActive))
            .ToArrayAsync(cancellationToken);
    }

    internal async Task<int> CountAssignmentsAsync(CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .ResponsibilityAssignments.CountAsync(cancellationToken);
    }

    internal async Task<int> CountEnablementsAsync(CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .PreparationEnablements.CountAsync(cancellationToken);
    }

    internal async Task<int> CountAdministrativeCommandsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .AdministrativeCommands.CountAsync(cancellationToken);
    }

    internal async Task<CredentialSnapshot?> ReadCredentialAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .LocalCredentials.AsNoTracking()
            .Where(credential => credential.IdentityId == identityId)
            .Select(credential => new CredentialSnapshot(
                credential.IdentityId,
                credential.LoginIdentifier,
                credential.NormalizedLoginIdentifier,
                credential.SecretVerifier))
            .SingleOrDefaultAsync(cancellationToken);
    }

    internal async Task<FunctionalResponsibility[]> ReadResponsibilitiesAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .ResponsibilityAssignments.AsNoTracking()
            .Where(assignment => assignment.IdentityId == identityId)
            .OrderBy(assignment => assignment.ResponsibilityCode)
            .Select(assignment => assignment.ResponsibilityCode)
            .ToArrayAsync(cancellationToken);
    }

    internal async Task<Guid[]> ReadEnablementsAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .PreparationEnablements.AsNoTracking()
            .Where(enablement => enablement.IdentityId == identityId)
            .OrderBy(enablement => enablement.PreparationResponsibilityId)
            .Select(enablement => enablement.PreparationResponsibilityId)
            .ToArrayAsync(cancellationToken);
    }

    internal async Task RemoveIdentityAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        var identity = await dbContext.Identities.SingleAsync(
            candidate => candidate.Id == identityId,
            cancellationToken);
        dbContext.Identities.Remove(identity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal async Task<bool> HasPendingModelChangesAsync()
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .Database.HasPendingModelChanges();
    }

    internal async Task<NpgsqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
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
                builder.UseSetting(
                    "ConnectionStrings:OperationalConfiguration", connectionString);
                builder.UseSetting("ConnectionStrings:OrderOperations", connectionString);
                builder.UseSetting(
                    "NexoBarSecurity:Cookies:SessionName",
                    "nexobar-session-test");
                builder.UseSetting(
                    "NexoBarSecurity:Cookies:AntiforgeryName",
                    "nexobar-antiforgery-test");
                builder.UseSetting("NexoBarSecurity:Cookies:Secure", "false");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(Clock);
                });
            });
        Client = application.CreateClient();
    }

    private static IdentitySnapshot Map(Identity identity) =>
        new(
            identity.Id,
            identity.OperationalName,
            identity.NormalizedOperationalName,
            identity.IsActive);
}

internal sealed record IdentitySnapshot(
    Guid Id,
    string OperationalName,
    string NormalizedOperationalName,
    bool IsActive);

internal sealed record CredentialSnapshot(
    Guid IdentityId,
    string LoginIdentifier,
    string NormalizedLoginIdentifier,
    string SecretVerifier);

internal sealed record SessionSnapshot(
    Guid Id,
    Guid IdentityId,
    byte[] TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset AbsoluteExpiresAt,
    DateTimeOffset? RevokedAt);

internal sealed class ManualTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset InitialUtcNow =
        new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private DateTimeOffset utcNow = InitialUtcNow;

    public override DateTimeOffset GetUtcNow() => utcNow;

    internal void Advance(TimeSpan interval) => utcNow += interval;

    internal void Reset() => utcNow = InitialUtcNow;
}

[CollectionDefinition(Name)]
public sealed class IdentitiesAndCapabilitiesCollection :
    ICollectionFixture<IdentitiesAndCapabilitiesFixture>
{
    public const string Name = "Identities and Capabilities with PostgreSQL";
}
