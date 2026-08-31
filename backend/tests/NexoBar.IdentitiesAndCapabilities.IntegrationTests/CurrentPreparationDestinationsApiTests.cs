using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class CurrentPreparationDestinationsApiTests(
    IdentitiesAndCapabilitiesFixture fixture)
{
    [Fact]
    public async Task Query_requires_authentication()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync(
            "/api/identity-sessions/current/preparation-destinations",
            token);

        await AssertProblemAsync(
            response,
            HttpStatusCode.Unauthorized,
            "identities_and_capabilities.authentication_required",
            token);
    }

    [Fact]
    public async Task Identity_without_Preparation_is_forbidden_and_remains_authenticated()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        await fixture.ProvisionCredentialAsync(identity.Id, "ana", "secret", token);
        using var client = fixture.CreateClient();
        using var login = await LoginAsync(client, "ana", "secret", token);
        login.EnsureSuccessStatusCode();

        using var response = await client.GetAsync(
            "/api/identity-sessions/current/preparation-destinations",
            token);

        await AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            "identities_and_capabilities.preparation.forbidden",
            token);
        using var current = await client.GetAsync(
            "/api/identity-sessions/current",
            token);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task Preparation_with_zero_enablements_returns_empty_list()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        await fixture.ProvisionCredentialAsync(identity.Id, "ana", "secret", token);
        await fixture.InsertAssignmentAsync(
            identity.Id,
            FunctionalResponsibility.Preparation,
            token);
        using var client = fixture.CreateClient();
        using var login = await LoginAsync(client, "ana", "secret", token);
        login.EnsureSuccessStatusCode();

        using var response = await client.GetAsync(
            "/api/identity-sessions/current/preparation-destinations",
            token);

        response.EnsureSuccessStatusCode();
        Assert.Empty(Assert.IsType<CurrentPreparationDestinationResponse[]>(
            await response.Content.ReadFromJsonAsync<
                CurrentPreparationDestinationResponse[]>(token)));
    }

    [Fact]
    public async Task Query_returns_one_enabled_destination_with_its_configuration_name()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var kitchen = await fixture.CreatePreparationResponsibilityAsync(
            "Cocina",
            token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        await fixture.ProvisionCredentialAsync(identity.Id, "ana", "secret", token);
        await fixture.InsertAssignmentAsync(
            identity.Id,
            FunctionalResponsibility.Preparation,
            token);
        await fixture.InsertEnablementAsync(identity.Id, kitchen, token);
        using var client = fixture.CreateClient();
        using var login = await LoginAsync(client, "ana", "secret", token);
        login.EnsureSuccessStatusCode();

        using var response = await client.GetAsync(
            "/api/identity-sessions/current/preparation-destinations",
            token);

        response.EnsureSuccessStatusCode();
        var destination = Assert.Single(
            Assert.IsType<CurrentPreparationDestinationResponse[]>(
                await response.Content.ReadFromJsonAsync<
                    CurrentPreparationDestinationResponse[]>(token)));
        Assert.Equal(kitchen, destination.PreparationResponsibilityId);
        Assert.Equal("Cocina", destination.OperationalName);
    }

    [Fact]
    public async Task Query_returns_only_enabled_destinations_with_configuration_names()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var kitchen = await fixture.CreatePreparationResponsibilityAsync(
            "Cocina",
            token);
        var bar = await fixture.CreatePreparationResponsibilityAsync("Barra", token);
        var unrelated = await fixture.CreatePreparationResponsibilityAsync(
            "Patio",
            token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        await fixture.ProvisionCredentialAsync(identity.Id, "ana", "secret", token);
        await fixture.InsertAssignmentAsync(
            identity.Id,
            FunctionalResponsibility.Preparation,
            token);
        await fixture.InsertEnablementAsync(identity.Id, kitchen, token);
        await fixture.InsertEnablementAsync(identity.Id, bar, token);
        using var client = fixture.CreateClient();
        using var login = await LoginAsync(client, "ana", "secret", token);
        login.EnsureSuccessStatusCode();

        using var response = await client.GetAsync(
            "/api/identity-sessions/current/preparation-destinations",
            token);

        response.EnsureSuccessStatusCode();
        var destinations = Assert.IsType<CurrentPreparationDestinationResponse[]>(
            await response.Content.ReadFromJsonAsync<
                CurrentPreparationDestinationResponse[]>(token));
        Assert.Equal(2, destinations.Length);
        Assert.Equal(
            [(bar, "Barra"), (kitchen, "Cocina")],
            destinations.Select(destination =>
                (destination.PreparationResponsibilityId,
                    destination.OperationalName)));
        Assert.DoesNotContain(
            destinations,
            destination => destination.PreparationResponsibilityId == unrelated);
    }

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string loginIdentifier,
        string secret,
        CancellationToken cancellationToken)
    {
        using var antiforgery = await client.GetAsync(
            "/api/security/antiforgery",
            cancellationToken);
        antiforgery.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await antiforgery.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var requestToken = document.RootElement.GetProperty("requestToken").GetString();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/identity-sessions")
        {
            Content = JsonContent.Create(new { loginIdentifier, secret })
        };
        request.Headers.Add("X-NexoBar-CSRF", requestToken);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
