using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NexoBar.OperationalConfiguration;

namespace NexoBar.OperationalConfiguration.IntegrationTests;

[Collection(OperationalConfigurationApiCollection.Name)]
public sealed class PreparationResponsibilityApiTests(
    OperationalConfigurationApiFixture fixture)
{
    private const string Route =
        "/api/operational-configuration/preparation-responsibilities";

    [Fact]
    public async Task Creates_trims_and_returns_a_uuid_v7_responsibility()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        using var response = await PostAsync(Guid.NewGuid(), "  Cocina  ", token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = Assert.IsType<PreparationResponsibilityResponse>(
            await response.Content.ReadFromJsonAsync<PreparationResponsibilityResponse>(token));
        Assert.Equal("Cocina", created.OperationalName);
        Assert.Equal(7, GetUuidVersion(created.Id));
        Assert.Equal((1, 1), await fixture.CountAsync(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_name_is_rejected(string operationalName)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var response = await PostAsync(Guid.NewGuid(), operationalName, token);
        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "operational_configuration.preparation_responsibility.operational_name_invalid",
            token);
        Assert.Equal((0, 0), await fixture.CountAsync(token));
    }

    [Fact]
    public async Task Duplicate_name_is_case_insensitive()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var first = await PostAsync(Guid.NewGuid(), "Cocina", token);
        first.EnsureSuccessStatusCode();
        using var duplicate = await PostAsync(Guid.NewGuid(), "cOcInA", token);
        await AssertProblemAsync(
            duplicate,
            HttpStatusCode.Conflict,
            "operational_configuration.preparation_responsibility.operational_name_conflict",
            token);
        Assert.Equal((1, 1), await fixture.CountAsync(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("018f4f9a-88dd-7ca2-8f25-38c9525f79b1")]
    public async Task Idempotency_key_is_required_and_must_be_uuid_v4(string? key)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new { operationalName = "Cocina" })
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }
        using var response = await fixture.Client.SendAsync(request, token);
        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            string.IsNullOrEmpty(key)
                ? "operational_configuration.preparation_responsibility.idempotency_key_required"
                : "operational_configuration.preparation_responsibility.idempotency_key_invalid",
            token);
    }

    [Fact]
    public async Task Same_key_and_trimmed_intent_replays_durable_result()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var key = Guid.NewGuid();
        using var first = await PostAsync(key, " Cocina ", token);
        var firstResult = await first.Content
            .ReadFromJsonAsync<PreparationResponsibilityResponse>(token);
        using var replay = await PostAsync(key, "Cocina", token);
        var replayResult = await replay.Content
            .ReadFromJsonAsync<PreparationResponsibilityResponse>(token);
        Assert.Equal(firstResult, replayResult);
        Assert.Equal((1, 1), await fixture.CountAsync(token));
    }

    [Fact]
    public async Task Same_key_and_different_intent_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var key = Guid.NewGuid();
        using var first = await PostAsync(key, "Cocina", token);
        first.EnsureSuccessStatusCode();
        using var conflict = await PostAsync(key, "Barra", token);
        await AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            "operational_configuration.preparation_responsibility.idempotency_key_conflict",
            token);
    }

    [Fact]
    public async Task Concurrent_same_key_creates_one_effect()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var key = Guid.NewGuid();
        var responses = await Task.WhenAll(
            PostAsync(key, "Cocina", token),
            PostAsync(key, "Cocina", token));
        try
        {
            Assert.All(responses, response =>
                Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            var results = await Task.WhenAll(responses.Select(response =>
                response.Content.ReadFromJsonAsync<PreparationResponsibilityResponse>(token)));
            Assert.Equal(results[0], results[1]);
            Assert.Equal((1, 1), await fixture.CountAsync(token));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Replay_survives_host_restart()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var key = Guid.NewGuid();
        using var first = await PostAsync(key, "Cocina", token);
        var expected = await first.Content
            .ReadFromJsonAsync<PreparationResponsibilityResponse>(token);
        await fixture.RestartAsync(token);
        using var replay = await PostAsync(key, "Cocina", token);
        Assert.Equal(
            expected,
            await replay.Content.ReadFromJsonAsync<PreparationResponsibilityResponse>(token));
    }

    [Fact]
    public async Task Command_failure_rolls_back_entity_and_allows_retry()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var key = Guid.NewGuid();
        await fixture.SetCommandFailureAsync(true, token);
        try
        {
            using var failed = await PostAsync(key, "Cocina", token);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal((0, 0), await fixture.CountAsync(token));
        }
        finally
        {
            await fixture.SetCommandFailureAsync(false, token);
        }
        using var retry = await PostAsync(key, "Cocina", token);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task Unknown_json_property_is_rejected()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(
                """{"operationalName":"Cocina","isActive":true}""",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await fixture.Client.SendAsync(request, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((0, 0), await fixture.CountAsync(token));
    }

    [Fact]
    public async Task Get_orders_by_operational_name_then_id()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var second = await PostAsync(Guid.NewGuid(), "Cocina", token);
        using var first = await PostAsync(Guid.NewGuid(), "Barra", token);
        second.EnsureSuccessStatusCode();
        first.EnsureSuccessStatusCode();
        var listed = await fixture.Client.GetFromJsonAsync<PreparationResponsibilityResponse[]>(
            Route,
            token);
        Assert.Equal(new[] { "Barra", "Cocina" }, listed!.Select(x => x.OperationalName));
    }

    [Fact]
    public async Task OpenApi_describes_create_and_list_contracts()
    {
        var token = TestContext.Current.CancellationToken;
        using var response = await fixture.Client.GetAsync("/openapi/v1.json", token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var path = document.RootElement.GetProperty("paths").GetProperty(Route);
        Assert.True(path.TryGetProperty("post", out var post));
        Assert.True(path.TryGetProperty("get", out _));
        Assert.Contains(
            post.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key" &&
                parameter.GetProperty("required").GetBoolean());
    }

    [Fact]
    public async Task Model_has_no_pending_changes()
    {
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    private async Task<HttpResponseMessage> PostAsync(
        Guid key,
        string operationalName,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new { operationalName })
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await fixture.Client.SendAsync(request, cancellationToken);
    }

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

    private static int GetUuidVersion(Guid value)
    {
        var bytes = value.ToByteArray(bigEndian: true);
        return bytes[6] >> 4;
    }
}
