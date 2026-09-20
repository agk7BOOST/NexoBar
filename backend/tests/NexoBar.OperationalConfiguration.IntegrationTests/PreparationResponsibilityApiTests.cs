using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OperationalConfiguration;

namespace NexoBar.OperationalConfiguration.IntegrationTests;

[Collection(OperationalConfigurationApiCollection.Name)]
public sealed class PreparationResponsibilityApiTests(
    OperationalConfigurationApiFixture fixture)
{
    private const string Route = "/api/operational-configuration/preparation-responsibilities";

    [Fact]
    public async Task Get_requires_authenticated_active_general_configuration_actor()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var anonymous = fixture.CreateClient();
        using var anonymousResponse = await anonymous.GetAsync(Route, token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var ordinary = await fixture.CreateActorAsync("Ordinary", true, FunctionalResponsibility.Preparation, token);
        using var ordinaryClient = fixture.CreateClient();
        await LoginAsync(ordinaryClient, ordinary, token);
        using var ordinaryResponse = await ordinaryClient.GetAsync(Route, token);
        Assert.Equal(HttpStatusCode.Forbidden, ordinaryResponse.StatusCode);

        var catalog = await fixture.CreateActorAsync("Catalog", true, FunctionalResponsibility.CatalogConfiguration, token);
        using var catalogClient = fixture.CreateClient();
        await LoginAsync(catalogClient, catalog, token);
        using var catalogResponse = await catalogClient.GetAsync(Route, token);
        Assert.Equal(HttpStatusCode.Forbidden, catalogResponse.StatusCode);

        var administrator = await fixture.CreateActorAsync("Administrator", true, FunctionalResponsibility.GeneralConfiguration, token);
        using var administratorClient = fixture.CreateClient();
        await LoginAsync(administratorClient, administrator, token);
        using var allowed = await administratorClient.GetAsync(Route, token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Post_requires_authentication_current_responsibility_and_antiforgery()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var anonymous = fixture.CreateClient();
        using var anonymousResponse = await SendCreateAsync(anonymous, Guid.NewGuid(), "Kitchen", token, antiforgery: false);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var wrong = await fixture.CreateActorAsync("Wrong", true, FunctionalResponsibility.CatalogConfiguration, token);
        using var wrongClient = fixture.CreateClient();
        await LoginAsync(wrongClient, wrong, token);
        using var forbidden = await SendCreateAsync(wrongClient, Guid.NewGuid(), "Kitchen", token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var administrator = await fixture.CreateActorAsync("Administrator", true, FunctionalResponsibility.GeneralConfiguration, token);
        using var client = fixture.CreateClient();
        await LoginAsync(client, administrator, token);
        using var missing = await SendCreateAsync(client, Guid.NewGuid(), "Kitchen", token, antiforgery: false);
        await AssertProblemAsync(missing, HttpStatusCode.BadRequest, "identities_and_capabilities.antiforgery_invalid", token);
        using var invalid = await SendCreateAsync(client, Guid.NewGuid(), "Kitchen", token, invalidToken: true);
        await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "identities_and_capabilities.antiforgery_invalid", token);
        using var created = await SendCreateAsync(client, Guid.NewGuid(), "Kitchen", token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task New_command_persists_server_derived_actor_and_rejects_client_attribution()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var administrator = await fixture.CreateActorAsync("Administrator", true, FunctionalResponsibility.GeneralConfiguration, token);
        using var client = fixture.CreateClient();
        await LoginAsync(client, administrator, token);
        var key = Guid.NewGuid();
        using var created = await SendCreateAsync(client, key, "Kitchen", token);
        created.EnsureSuccessStatusCode();
        Assert.Equal(administrator.IdentityId, await fixture.ReadCommandActorAsync(key, token));

        var csrf = await GetAntiforgeryAsync(client, token);
        using var attributed = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent($"{{\"operationalName\":\"Bar\",\"actorIdentityId\":\"{Guid.NewGuid():D}\"}}", Encoding.UTF8, "application/json")
        };
        attributed.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        attributed.Headers.Add("X-NexoBar-CSRF", csrf);
        using var rejected = await client.SendAsync(attributed, token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Same_actor_same_key_and_intent_replays_without_second_effect()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync("Administrator", true, FunctionalResponsibility.GeneralConfiguration, token);
        using var client = fixture.CreateClient();
        await LoginAsync(client, actor, token);
        var key = Guid.NewGuid();
        using var first = await SendCreateAsync(client, key, " Kitchen ", token);
        var original = await first.Content.ReadFromJsonAsync<PreparationResponsibilityResponse>(token);
        using var replay = await SendCreateAsync(client, key, "Kitchen", token);
        var replayed = await replay.Content.ReadFromJsonAsync<PreparationResponsibilityResponse>(token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original, replayed);
        Assert.Equal((1, 1), await fixture.CountAsync(token));
    }

    [Fact]
    public async Task Same_key_with_another_actor_or_intent_or_legacy_command_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var firstActor = await fixture.CreateActorAsync("First", true, FunctionalResponsibility.GeneralConfiguration, token);
        var secondActor = await fixture.CreateActorAsync("Second", true, FunctionalResponsibility.GeneralConfiguration, token);
        using var firstClient = fixture.CreateClient();
        using var secondClient = fixture.CreateClient();
        await LoginAsync(firstClient, firstActor, token);
        await LoginAsync(secondClient, secondActor, token);
        var key = Guid.NewGuid();
        using var created = await SendCreateAsync(firstClient, key, "Kitchen", token);
        created.EnsureSuccessStatusCode();
        using var otherActor = await SendCreateAsync(secondClient, key, "Kitchen", token);
        await AssertConflictAsync(otherActor, token);
        using var changedIntent = await SendCreateAsync(firstClient, key, "Bar", token);
        await AssertConflictAsync(changedIntent, token);

        var legacyKey = Guid.NewGuid();
        await fixture.InsertLegacyCommandAsync(legacyKey, Guid.CreateVersion7(), "Legacy", token);
        using var legacyReplay = await SendCreateAsync(firstClient, legacyKey, "Legacy", token);
        await AssertConflictAsync(legacyReplay, token);
    }

    [Fact]
    public async Task Exact_replay_survives_general_configuration_revocation_but_new_command_does_not()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync("Administrator", true, FunctionalResponsibility.GeneralConfiguration, token);
        using var client = fixture.CreateClient();
        await LoginAsync(client, actor, token);
        var key = Guid.NewGuid();
        using var committed = await SendCreateAsync(client, key, "Kitchen", token);
        committed.EnsureSuccessStatusCode();
        await fixture.RemoveResponsibilityAsync(actor.IdentityId, FunctionalResponsibility.GeneralConfiguration, token);
        using var rejectedNew = await SendCreateAsync(client, Guid.NewGuid(), "Bar", token);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedNew.StatusCode);
        using var replay = await SendCreateAsync(client, key, "Kitchen", token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal((1, 1), await fixture.CountAsync(token));
    }

    [Fact]
    public async Task Inactive_identity_or_revoked_session_cannot_replay()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync("Administrator", true, FunctionalResponsibility.GeneralConfiguration, token);
        using var client = fixture.CreateClient();
        await LoginAsync(client, actor, token);
        var key = Guid.NewGuid();
        using var committed = await SendCreateAsync(client, key, "Kitchen", token);
        committed.EnsureSuccessStatusCode();
        await fixture.DeactivateAsync(actor.IdentityId, token);
        using var inactiveReplay = await SendCreateAsync(client, key, "Kitchen", token);
        Assert.Equal(HttpStatusCode.Unauthorized, inactiveReplay.StatusCode);

        await fixture.ResetAsync(token);
        actor = await fixture.CreateActorAsync("Administrator", true, FunctionalResponsibility.GeneralConfiguration, token);
        using var renewedClient = fixture.CreateClient();
        await LoginAsync(renewedClient, actor, token);
        key = Guid.NewGuid();
        using var newCommitted = await SendCreateAsync(renewedClient, key, "Kitchen", token);
        newCommitted.EnsureSuccessStatusCode();
        await fixture.RevokeSessionsAsync(actor.IdentityId, token);
        using var revokedReplay = await SendCreateAsync(renewedClient, key, "Kitchen", token);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedReplay.StatusCode);
    }

    [Fact]
    public async Task Case_insensitive_uniqueness_and_model_migration_are_preserved()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync("Administrator", true, FunctionalResponsibility.GeneralConfiguration, token);
        using var client = fixture.CreateClient();
        await LoginAsync(client, actor, token);
        using var first = await SendCreateAsync(client, Guid.NewGuid(), "Kitchen", token);
        first.EnsureSuccessStatusCode();
        using var duplicate = await SendCreateAsync(client, Guid.NewGuid(), "kItChEn", token);
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "operational_configuration.preparation_responsibility.operational_name_conflict", token);
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    [Fact]
    public async Task Actor_column_migration_preserves_legacy_null_and_round_trips_down()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var legacyKey = Guid.NewGuid();
        await fixture.InsertLegacyCommandAsync(legacyKey, Guid.CreateVersion7(), "Legacy", token);
        Assert.Null(await fixture.ReadCommandActorAsync(legacyKey, token));
        await fixture.VerifyActorMigrationRoundTripAsync(token);
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    private static async Task LoginAsync(HttpClient client, TestActor actor, CancellationToken cancellationToken)
    {
        var csrf = await GetAntiforgeryAsync(client, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity-sessions")
        {
            Content = JsonContent.Create(new { loginIdentifier = actor.LoginIdentifier, secret = actor.Secret })
        };
        request.Headers.Add("X-NexoBar-CSRF", csrf);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> GetAntiforgeryAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/api/security/antiforgery", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendCreateAsync(HttpClient client, Guid key, string name, CancellationToken cancellationToken, bool antiforgery = true, bool invalidToken = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new { operationalName = name })
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        if (antiforgery)
        {
            request.Headers.Add("X-NexoBar-CSRF", invalidToken ? "invalid" : await GetAntiforgeryAsync(client, cancellationToken));
        }
        return await client.SendAsync(request, cancellationToken);
    }

    private static Task AssertConflictAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        AssertProblemAsync(response, HttpStatusCode.Conflict, "operational_configuration.preparation_responsibility.idempotency_key_conflict", cancellationToken);

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
