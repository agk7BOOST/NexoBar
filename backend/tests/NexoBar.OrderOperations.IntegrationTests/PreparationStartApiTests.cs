using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PreparationStartApiTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Start_moves_quantity_and_persists_authoritative_history_and_command()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        var created = await fixture.CreatePreparedWorkAsync(
            responsibility,
            token,
            quantity: 5);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var actor = await fixture.CreatePreparationActorAsync(
            hasPreparation: true,
            responsibility,
            token);
        using var client = await fixture.LoginAsync(actor, token);
        var key = Guid.NewGuid();

        using var response = await PreparationStartTestSupport.PostAsync(
            client,
            work.Id,
            key,
            2,
            token);
        var result = await PreparationStartTestSupport.ReadSuccessAsync(response, token);

        Assert.Equal(work.Id, result.WorkId);
        Assert.Equal(7, result.HistoryId.ToByteArray(bigEndian: true)[6] >> 4);
        Assert.Equal(TimeSpan.Zero, result.OccurredAt.Offset);
        Assert.Equal(5, result.TotalQuantity);
        Assert.Equal(3, result.PendingQuantity);
        Assert.Equal(2, result.InPreparationQuantity);
        Assert.Equal(0, result.ReadyQuantity);

        var persistedWork = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(5, persistedWork.TotalQuantity);
        Assert.Equal(3, persistedWork.PendingQuantity);
        Assert.Equal(2, persistedWork.InPreparationQuantity);
        Assert.Equal(0, persistedWork.ReadyQuantity);
        Assert.Equal(created.Product.Id, persistedWork.ProductId);
        Assert.Null(persistedWork.Instruction);

        var snapshot = await fixture.ReadSnapshotAsync(token);
        Assert.Equal(5, Assert.Single(snapshot.Contents).Quantity);

        var history = Assert.Single(await fixture.ReadPreparationHistoryAsync(token));
        Assert.Equal(result.HistoryId, history.Id);
        Assert.Equal(work.Id, history.WorkId);
        Assert.Equal(PreparationHistory.QuantityStartedEventKind, history.EventKind);
        Assert.Equal(2, history.Quantity);
        Assert.Equal(actor.IdentityId, history.ActorIdentityId);
        Assert.Equal(result.OccurredAt, history.OccurredAt);
        Assert.Equal(5, history.ResultingTotalQuantity);
        Assert.Equal(3, history.ResultingPendingQuantity);
        Assert.Equal(2, history.ResultingInPreparationQuantity);
        Assert.Equal(0, history.ResultingReadyQuantity);

        var command = Assert.Single(await fixture.ReadPreparationCommandsAsync(token));
        Assert.Equal(key, command.IdempotencyKey);
        Assert.Equal(actor.IdentityId, command.ActorIdentityId);
        Assert.Equal(PreparationCommand.StartQuantityCommandKind, command.CommandKind);
        Assert.Equal(work.Id, command.WorkId);
        Assert.Equal(2, command.Quantity);
        Assert.Equal(result.HistoryId, command.ResultHistoryId);
        Assert.Equal(result.OccurredAt, command.ResultOccurredAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_quantity_is_bad_request_without_effects(int quantity)
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token, quantity: 5);
        using var client = scenario.Client;

        using var response = await PreparationStartTestSupport.PostAsync(
            client,
            scenario.Work.Id,
            Guid.NewGuid(),
            quantity,
            token);

        await PreparationStartTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.preparation_start.quantity_invalid",
            token);
        await AssertNoPreparationEffectsAsync(5, token);
    }

    [Fact]
    public async Task Insufficient_pending_quantity_is_conflict_without_clipping()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token, quantity: 3);
        using var client = scenario.Client;

        using var response = await PreparationStartTestSupport.PostAsync(
            client,
            scenario.Work.Id,
            Guid.NewGuid(),
            4,
            token);

        await PreparationStartTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.preparation_start.pending_quantity_insufficient",
            token);
        await AssertNoPreparationEffectsAsync(3, token);
    }

    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var response = await PreparationStartTestSupport.PostAsync(
            fixture.Client,
            Guid.CreateVersion7(),
            Guid.NewGuid(),
            1,
            token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_session_is_unauthorized()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        await fixture.RevokeSessionsAsync(scenario.Actor.IdentityId, token);

        using var response = await PreparationStartTestSupport.PostAsync(
            client,
            scenario.Work.Id,
            Guid.NewGuid(),
            1,
            token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoPreparationEffectsAsync(5, token);
    }

    [Fact]
    public async Task Inactive_identity_is_unauthorized()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        await fixture.SetIdentityActiveAsync(scenario.Actor.IdentityId, false, token);

        using var response = await PreparationStartTestSupport.PostAsync(
            client,
            scenario.Work.Id,
            Guid.NewGuid(),
            1,
            token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoPreparationEffectsAsync(5, token);
    }

    [Fact]
    public async Task Missing_preparation_responsibility_is_forbidden_before_work_lookup()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity: 5);
        var actor = await fixture.CreatePreparationActorAsync(
            hasPreparation: false,
            responsibility,
            token);
        using var client = await fixture.LoginAsync(actor, token);

        using var existing = await PreparationStartTestSupport.PostAsync(
            client,
            Assert.Single(await fixture.ReadPreparationWorkAsync(token)).Id,
            Guid.NewGuid(),
            1,
            token);
        using var absent = await PreparationStartTestSupport.PostAsync(
            client,
            Guid.CreateVersion7(),
            Guid.NewGuid(),
            1,
            token);

        await PreparationStartTestSupport.AssertProblemAsync(
            existing,
            HttpStatusCode.Forbidden,
            "order_operations.preparation.forbidden",
            token);
        await PreparationStartTestSupport.AssertProblemAsync(
            absent,
            HttpStatusCode.Forbidden,
            "order_operations.preparation.forbidden",
            token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_wrong_enablement_is_indistinguishable_from_absent_work(
        bool grantWrongEnablement)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity: 5);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var actor = await fixture.CreatePreparationActorAsync(
            hasPreparation: true,
            grantWrongEnablement ? Guid.CreateVersion7() : null,
            token);
        using var client = await fixture.LoginAsync(actor, token);

        using var hidden = await PreparationStartTestSupport.PostAsync(
            client,
            work.Id,
            Guid.NewGuid(),
            1,
            token);
        using var absent = await PreparationStartTestSupport.PostAsync(
            client,
            Guid.CreateVersion7(),
            Guid.NewGuid(),
            1,
            token);

        await PreparationStartTestSupport.AssertProblemAsync(
            hidden,
            HttpStatusCode.NotFound,
            "order_operations.preparation_work.not_found",
            token);
        await PreparationStartTestSupport.AssertProblemAsync(
            absent,
            HttpStatusCode.NotFound,
            "order_operations.preparation_work.not_found",
            token);
        await AssertNoPreparationEffectsAsync(5, token);
    }

    [Theory]
    [InlineData("actorIdentityId")]
    [InlineData("preparationResponsibilityId")]
    [InlineData("status")]
    public async Task Authority_or_status_fields_in_body_are_rejected(string field)
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var json = $$"""
            { "quantity": 1, "{{field}}": "{{Guid.CreateVersion7():D}}" }
            """;

        using var response = await PreparationStartTestSupport.PostRawAsync(
            client,
            scenario.Work.Id,
            Guid.NewGuid(),
            json,
            token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoPreparationEffectsAsync(5, token);
    }

    [Fact]
    public async Task Malformed_json_is_bad_request_without_effects()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;

        using var response = await PreparationStartTestSupport.PostRawAsync(
            client,
            scenario.Work.Id,
            Guid.NewGuid(),
            "{ not-json",
            token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoPreparationEffectsAsync(5, token);
    }

    [Theory]
    [InlineData(null, "order_operations.preparation_start.idempotency_key_required")]
    [InlineData("not-a-uuid", "order_operations.preparation_start.idempotency_key_invalid")]
    [InlineData("018f4f3c-5e34-7abc-8def-1234567890ab", "order_operations.preparation_start.idempotency_key_invalid")]
    public async Task Missing_or_invalid_idempotency_key_is_bad_request(
        string? key,
        string code)
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;

        using var response = await PreparationStartTestSupport.PostWithRawIdentifiersAsync(
            client,
            scenario.Work.Id.ToString("D"),
            key,
            1,
            token);

        await PreparationStartTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            code,
            token);
        await AssertNoPreparationEffectsAsync(5, token);
    }

    [Fact]
    public async Task Malformed_work_id_is_bad_request()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;

        using var response = await PreparationStartTestSupport.PostWithRawIdentifiersAsync(
            client,
            "not-a-work-id",
            Guid.NewGuid().ToString("D"),
            1,
            token);

        await PreparationStartTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.preparation_start.work_id_invalid",
            token);
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_bad_request()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/preparation/work/{scenario.Work.Id:D}/start")
        {
            Content = JsonContent.Create(new StartPreparationQuantityRequest(1))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));

        using var response = await client.SendAsync(request, token);

        await PreparationStartTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.preparation_start.antiforgery_invalid",
            token);
        await AssertNoPreparationEffectsAsync(5, token);
    }

    [Fact]
    public async Task Occurred_at_uses_the_backend_time_provider()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity: 2);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var actor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        var expected = new DateTimeOffset(2026, 8, 31, 12, 34, 56, TimeSpan.Zero);
        await using var application = fixture.CreateApplicationWithTimeProvider(
            new FixedTimeProvider(expected));
        using var client = await fixture.LoginAsync(actor, token, application);

        using var response = await PreparationStartTestSupport.PostAsync(
            client,
            work.Id,
            Guid.NewGuid(),
            1,
            token);
        var result = await PreparationStartTestSupport.ReadSuccessAsync(response, token);

        Assert.Equal(expected, result.OccurredAt);
        Assert.Equal(expected, Assert.Single(
            await fixture.ReadPreparationHistoryAsync(token)).OccurredAt);
    }

    [Fact]
    public async Task Command_insert_failure_rolls_back_work_history_and_command()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        await fixture.SetPreparationCommandFailureAsync(true, token);
        try
        {
            using var response = await PreparationStartTestSupport.PostAsync(
                client,
                scenario.Work.Id,
                Guid.NewGuid(),
                2,
                token);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await AssertNoPreparationEffectsAsync(5, token);
        }
        finally
        {
            await fixture.SetPreparationCommandFailureAsync(false, token);
        }
    }

    [Fact]
    public async Task OpenApi_describes_start_contract()
    {
        var token = TestContext.Current.CancellationToken;
        using var response = await fixture.Client.GetAsync("/openapi/v1.json", token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(token),
            cancellationToken: token);
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/order-operations/preparation/work/{workId}/start")
            .GetProperty("post");

        Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("404", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("409", out _));
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Contains(parameters, parameter =>
            parameter.GetProperty("name").GetString() == "workId" &&
            parameter.GetProperty("in").GetString() == "path" &&
            parameter.GetProperty("required").GetBoolean());
        Assert.Contains(parameters, parameter =>
            parameter.GetProperty("name").GetString() == "Idempotency-Key" &&
            parameter.GetProperty("in").GetString() == "header" &&
            parameter.GetProperty("required").GetBoolean());
    }

    private async Task<StartScenario> CreateScenarioAsync(
        CancellationToken cancellationToken,
        int quantity = 5)
    {
        await fixture.ResetAsync(cancellationToken);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(
            responsibility,
            cancellationToken,
            quantity);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(cancellationToken));
        var actor = await fixture.CreatePreparationActorAsync(
            hasPreparation: true,
            responsibility,
            cancellationToken);
        var client = await fixture.LoginAsync(actor, cancellationToken);
        return new StartScenario(actor, client, work);
    }

    private async Task AssertNoPreparationEffectsAsync(
        int expectedPending,
        CancellationToken cancellationToken)
    {
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(cancellationToken));
        Assert.Equal(expectedPending, work.PendingQuantity);
        Assert.Equal(0, work.InPreparationQuantity);
        Assert.Equal(0, work.ReadyQuantity);
        Assert.Empty(await fixture.ReadPreparationHistoryAsync(cancellationToken));
        Assert.Empty(await fixture.ReadPreparationCommandsAsync(cancellationToken));
    }

    private sealed record StartScenario(
        PreparationActor Actor,
        HttpClient Client,
        PreparationWorkSnapshot Work);
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
