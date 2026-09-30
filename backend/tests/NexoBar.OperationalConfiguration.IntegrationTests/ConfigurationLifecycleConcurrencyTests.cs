using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OperationalConfiguration.IntegrationTests;

public sealed partial class ConfigurationLifecycleApiTests
{
    [Fact]
    public async Task Context_delete_waits_for_first_confirmation_and_rejects_its_committed_participation()
    {
        await fixture.ResetAsync(Token);
        var actor = await ActorAsync();
        using var client = await LoginAsync(actor);
        var context = await CreateAsync(client, Contexts, "Race context");
        var product = await ProductAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = fixture.DecorateServices(services =>
        {
            services.RemoveAll<IOrderContextConfiguration>();
            services.AddScoped<IOrderContextConfiguration>(provider => new HoldingContext(
                new OperationalContextLookup(provider.GetRequiredService<OperationalConfigurationDbContext>()), reached, release));
        });
        using var confirmationClient = app.CreateClient();
        using var login = await SendAsync(confirmationClient, HttpMethod.Post, "/api/identity-sessions", new { loginIdentifier = actor.LoginIdentifier, secret = actor.Secret }, null);
        login.EnsureSuccessStatusCode();
        var confirmation = ConfirmAsync(confirmationClient, context.Id, product);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var deletion = CommandAsync(client, Contexts, context.Id, "delete", new { expectedCurrentOperationalName = "Race context", expectedIsActive = true }, Guid.NewGuid());
            await WaitForConfigurationLockAsync("contexts");
            release.TrySetResult();
            await confirmation.WaitAsync(TimeSpan.FromSeconds(10), Token);
            using var result = await deletion.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await ProblemAsync(result, "delete.operational_participation");
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Destination_retire_waits_for_product_configuration_or_reactivation_and_rejects_active_reference(bool reactivation)
    {
        await fixture.ResetAsync(Token);
        var actor = await ActorAsync();
        using var client = await LoginAsync(actor);
        var destination = await CreateAsync(client, Destinations, "Race destination");
        var product = await ProductAsync();
        if (reactivation)
        {
            await ConfigureProductAsync(client, product, null, destination.Id);
            using var retiredProduct = await SendAsync(client, HttpMethod.Post, $"/api/catalog/products/{product}/retire", null, Guid.NewGuid());
            retiredProduct.EnsureSuccessStatusCode();
        }
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = fixture.DecorateServices(services =>
        {
            services.RemoveAll<IPreparationResponsibilityLookup>();
            services.AddScoped<IPreparationResponsibilityLookup>(provider => new HoldingDestination(
                new PreparationResponsibilityLookup(provider.GetRequiredService<OperationalConfigurationDbContext>()), reached, release));
        });
        using var configurationClient = app.CreateClient();
        using var login = await SendAsync(configurationClient, HttpMethod.Post, "/api/identity-sessions", new { loginIdentifier = actor.LoginIdentifier, secret = actor.Secret }, null);
        login.EnsureSuccessStatusCode();
        var mutation = reactivation
            ? SendAsync(configurationClient, HttpMethod.Post, $"/api/catalog/products/{product}/reactivate", null, Guid.NewGuid())
            : SendAsync(configurationClient, HttpMethod.Post, $"/api/catalog/products/{product}/preparation-configuration-changes", new { expectedCurrentPreparationResponsibilityId = (Guid?)null, newPreparationResponsibilityId = destination.Id }, Guid.NewGuid());
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var retire = CommandAsync(client, Destinations, destination.Id, "retire", new { expectedCurrentOperationalName = "Race destination", expectedIsActive = true }, Guid.NewGuid());
            await WaitForConfigurationLockAsync("preparation_responsibilities");
            release.TrySetResult();
            using var configured = await mutation.WaitAsync(TimeSpan.FromSeconds(10), Token);
            configured.EnsureSuccessStatusCode();
            using var result = await retire.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await ProblemAsync(result, "retire.active_products");
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Destination_delete_waits_for_enablement_grant_and_cannot_leave_dangling_reference()
    {
        await fixture.ResetAsync(Token);
        var actor = await ActorAsync();
        var target = await fixture.CreateActorAsync("Grant target", true, null, Token);
        using var client = await LoginAsync(actor);
        var destination = await CreateAsync(client, Destinations, "Grant race");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = fixture.DecorateServices(services =>
        {
            services.RemoveAll<IPreparationResponsibilityLookup>();
            services.AddScoped<IPreparationResponsibilityLookup>(provider => new HoldingDestination(
                new PreparationResponsibilityLookup(provider.GetRequiredService<OperationalConfigurationDbContext>()), reached, release));
        });
        using var grantClient = app.CreateClient();
        using var login = await SendAsync(grantClient, HttpMethod.Post, "/api/identity-sessions", new { loginIdentifier = actor.LoginIdentifier, secret = actor.Secret }, null);
        login.EnsureSuccessStatusCode();
        var grant = SendAsync(grantClient, HttpMethod.Post, $"/api/identities/{target.IdentityId}/preparation-enablement/{destination.Id}/grant", null, Guid.NewGuid());
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var deletion = CommandAsync(client, Destinations, destination.Id, "delete", new { expectedCurrentOperationalName = "Grant race", expectedIsActive = true }, Guid.NewGuid());
            await WaitForConfigurationLockAsync("preparation_responsibilities");
            release.TrySetResult();
            using var granted = await grant.WaitAsync(TimeSpan.FromSeconds(10), Token);
            granted.EnsureSuccessStatusCode();
            using var result = await deletion.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await ProblemAsync(result, "delete.preparation_enablements");
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(Contexts)]
    [InlineData(Destinations)]
    public async Task Command_persistence_failure_rolls_back_state_and_same_intention_can_retry(string entity)
    {
        await fixture.ResetAsync(Token);
        using var client = await LoginAsync(await ActorAsync());
        var item = await CreateAsync(client, entity, "Atomic lifecycle");
        var table = entity == Contexts ? "context_lifecycle_commands" : "preparation_responsibility_lifecycle_commands";
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OperationalConfigurationDbContext>();
        var trigger = $"CREATE FUNCTION operational_configuration.fail_lifecycle() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'controlled lifecycle failure'; END; $$; CREATE TRIGGER fail_lifecycle BEFORE INSERT ON operational_configuration.{table} FOR EACH ROW EXECUTE FUNCTION operational_configuration.fail_lifecycle();";
        await db.Database.ExecuteSqlRawAsync(trigger, Token);
        var key = Guid.NewGuid();
        var body = new { expectedCurrentOperationalName = "Atomic lifecycle", expectedIsActive = true };
        try
        {
            using var failed = await CommandAsync(client, entity, item.Id, "retire", body, key);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.True(Assert.Single((await client.GetFromJsonAsync<OperationalContextReference[]>(Route(entity), Token))!).IsActive);
        }
        finally
        {
            var cleanup = $"DROP TRIGGER fail_lifecycle ON operational_configuration.{table}; DROP FUNCTION operational_configuration.fail_lifecycle();";
            await db.Database.ExecuteSqlRawAsync(cleanup, Token);
        }
        using var retry = await CommandAsync(client, entity, item.Id, "retire", body, key);
        retry.EnsureSuccessStatusCode();
        Assert.False((await retry.Content.ReadFromJsonAsync<ConfigurationLifecycleResponse>(Token))!.IsActive);
    }

    private async Task WaitForConfigurationLockAsync(string table)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OperationalConfigurationDbContext>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var pattern = $"%operational_configuration.{table}%";
            if (await db.Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE {pattern} AND query LIKE '%FOR UPDATE%'").SingleAsync(timeout.Token) > 0)
                return;
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class HoldingContext(IOrderContextConfiguration inner, TaskCompletionSource reached, TaskCompletionSource release) : IOrderContextConfiguration
    {
        public Task<ConfiguredOrderContext?> ResolveConfiguredContextAsync(Guid id, CancellationToken token) => inner.ResolveConfiguredContextAsync(id, token);
        public Task<IReadOnlyList<OperationalContextReference>> ListConfiguredContextsAsync(CancellationToken token) => inner.ListConfiguredContextsAsync(token);
        public async Task<ConfiguredOrderContext?> ResolveConfiguredContextAsync(Guid id, DbTransaction transaction, CancellationToken token)
        {
            var result = await inner.ResolveConfiguredContextAsync(id, transaction, token);
            reached.TrySetResult();
            await release.Task.WaitAsync(token);
            return result;
        }
    }
    private sealed class HoldingDestination(IPreparationResponsibilityLookup inner, TaskCompletionSource reached, TaskCompletionSource release) : IPreparationResponsibilityLookup
    {
        public Task<bool> ExistsAsync(Guid id, CancellationToken token) => inner.ExistsAsync(id, token);
        public Task<IReadOnlyList<PreparationResponsibilityReference>> ListAsync(CancellationToken token) => inner.ListAsync(token);
        public Task<IReadOnlyList<PreparationResponsibilityReference>> ListActiveAsync(CancellationToken token) => inner.ListActiveAsync(token);
        public Task<IReadOnlyList<PreparationResponsibilityReference>> ReadByIdsAsync(IReadOnlyCollection<Guid> ids, DbTransaction transaction, CancellationToken token) => inner.ReadByIdsAsync(ids, transaction, token);
        public async Task<bool> ExistsAsync(Guid id, DbTransaction transaction, CancellationToken token)
        {
            var result = await inner.ExistsAsync(id, transaction, token);
            reached.TrySetResult(); await release.Task.WaitAsync(token); return result;
        }
        public async Task<bool> IsActiveAsync(Guid id, DbTransaction transaction, CancellationToken token)
        {
            var result = await inner.IsActiveAsync(id, transaction, token);
            reached.TrySetResult(); await release.Task.WaitAsync(token); return result;
        }
    }
}
