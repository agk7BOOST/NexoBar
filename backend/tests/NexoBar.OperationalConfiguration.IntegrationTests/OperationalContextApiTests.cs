using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OperationalConfiguration;

namespace NexoBar.OperationalConfiguration.IntegrationTests;

[Collection(OperationalConfigurationApiCollection.Name)]
public sealed class OperationalContextApiTests(
    OperationalConfigurationApiFixture fixture)
{
    private const string Route = "/api/operational-configuration/contexts";

    [Fact]
    public async Task General_configuration_creates_trimmed_uuid_v7_context_and_list_returns_it()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "Context administrator",
            true,
            FunctionalResponsibility.GeneralConfiguration,
            token);
        using var client = fixture.CreateClient();
        await LoginAsync(client, actor, token);

        using var created = await SendCreateAsync(client, Guid.NewGuid(), "  Mesa 1  ", token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var result = Assert.IsType<OperationalContextReference>(
            await created.Content.ReadFromJsonAsync<OperationalContextReference>(token));
        Assert.Equal("Mesa 1", result.OperationalName);
        Assert.Equal('7', result.Id.ToString("N")[12]);

        using var listed = await client.GetAsync(Route, token);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var contexts = await listed.Content.ReadFromJsonAsync<OperationalContextReference[]>(token);
        Assert.Equal(result, Assert.Single(contexts!));
    }

    [Fact]
    public async Task Create_replay_precedes_revocation_and_conflicts_for_other_actor_or_intent()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var first = await fixture.CreateActorAsync(
            "First context administrator",
            true,
            FunctionalResponsibility.GeneralConfiguration,
            token);
        var second = await fixture.CreateActorAsync(
            "Second context administrator",
            true,
            FunctionalResponsibility.GeneralConfiguration,
            token);
        using var firstClient = fixture.CreateClient();
        using var secondClient = fixture.CreateClient();
        await LoginAsync(firstClient, first, token);
        await LoginAsync(secondClient, second, token);

        var key = Guid.NewGuid();
        using var original = await SendCreateAsync(firstClient, key, "Mesa", token);
        var originalContext = await original.Content.ReadFromJsonAsync<OperationalContextReference>(token);
        original.EnsureSuccessStatusCode();
        await fixture.RemoveResponsibilityAsync(
            first.IdentityId,
            FunctionalResponsibility.GeneralConfiguration,
            token);

        using var replay = await SendCreateAsync(firstClient, key, " Mesa ", token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(
            originalContext,
            await replay.Content.ReadFromJsonAsync<OperationalContextReference>(token));
        using var otherActor = await SendCreateAsync(secondClient, key, "Mesa", token);
        await AssertConflictAsync(otherActor, token);
        using var changedIntent = await SendCreateAsync(firstClient, key, "Barra", token);
        await AssertConflictAsync(changedIntent, token);
    }

    [Fact]
    public async Task Blank_duplicate_and_non_general_configuration_authority_are_rejected()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var administrator = await fixture.CreateActorAsync(
            "Administrator context",
            true,
            FunctionalResponsibility.GeneralConfiguration,
            token);
        var ordinary = await fixture.CreateActorAsync(
            "Ordinary context",
            true,
            FunctionalResponsibility.Preparation,
            token);
        using var adminClient = fixture.CreateClient();
        using var ordinaryClient = fixture.CreateClient();
        await LoginAsync(adminClient, administrator, token);
        await LoginAsync(ordinaryClient, ordinary, token);

        using var blank = await SendCreateAsync(adminClient, Guid.NewGuid(), " \t ", token);
        await AssertProblemAsync(
            blank,
            HttpStatusCode.BadRequest,
            "operational_configuration.context.operational_name_invalid",
            token);
        using var original = await SendCreateAsync(adminClient, Guid.NewGuid(), "Mesa", token);
        original.EnsureSuccessStatusCode();
        using var duplicate = await SendCreateAsync(adminClient, Guid.NewGuid(), " mEsA ", token);
        await AssertProblemAsync(
            duplicate,
            HttpStatusCode.Conflict,
            "operational_configuration.context.operational_name_conflict",
            token);
        using var forbiddenCreate = await SendCreateAsync(ordinaryClient, Guid.NewGuid(), "Barra", token);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenCreate.StatusCode);
        using var forbiddenList = await ordinaryClient.GetAsync(Route, token);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenList.StatusCode);
    }

    private static async Task LoginAsync(
        HttpClient client,
        TestActor actor,
        CancellationToken cancellationToken)
    {
        var csrf = await GetAntiforgeryAsync(client, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity-sessions")
        {
            Content = JsonContent.Create(new
            {
                loginIdentifier = actor.LoginIdentifier,
                secret = actor.Secret
            })
        };
        request.Headers.Add("X-NexoBar-CSRF", csrf);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> GetAntiforgeryAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/api/security/antiforgery", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendCreateAsync(
        HttpClient client,
        Guid key,
        string name,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new { operationalName = name })
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        request.Headers.Add("X-NexoBar-CSRF", await GetAntiforgeryAsync(client, cancellationToken));
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task AssertConflictAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "operational_configuration.context.idempotency_key_conflict",
            cancellationToken);

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken));
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
