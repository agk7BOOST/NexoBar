using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PreparationReadyApiTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Ready_moves_exact_partial_quantity_and_persists_one_ready_history()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 4, token);
        using var client = scenario.Client;
        using (var firstReadyResponse = await PreparationReadyTestSupport.PostAsync(
                   client, scenario.Work.Id, Guid.NewGuid(), 1, token))
        {
            await PreparationReadyTestSupport.ReadSuccessAsync(firstReadyResponse, token);
        }

        using var response = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, Guid.NewGuid(), 2, token);
        var result = await PreparationReadyTestSupport.ReadSuccessAsync(response, token);

        Assert.Equal(scenario.Work.Id, result.WorkId);
        Assert.Equal(7, result.HistoryId.ToByteArray(bigEndian: true)[6] >> 4);
        Assert.Equal(TimeSpan.Zero, result.OccurredAt.Offset);
        Assert.Equal(5, result.TotalQuantity);
        Assert.Equal(1, result.PendingQuantity);
        Assert.Equal(1, result.InPreparationQuantity);
        Assert.Equal(3, result.ReadyQuantity);

        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(5, work.TotalQuantity);
        Assert.Equal(1, work.PendingQuantity);
        Assert.Equal(1, work.InPreparationQuantity);
        Assert.Equal(3, work.ReadyQuantity);

        var history = (await fixture.ReadPreparationHistoryAsync(token))
            .Single(item => item.Id == result.HistoryId);
        Assert.Equal(PreparationHistory.QuantityReadyEventKind, history.EventKind);
        Assert.Equal(2, history.Quantity);
        Assert.Equal(scenario.Actor.IdentityId, history.ActorIdentityId);
        Assert.Equal(result.OccurredAt, history.OccurredAt);
        Assert.Equal(5, history.ResultingTotalQuantity);
        Assert.Equal(1, history.ResultingPendingQuantity);
        Assert.Equal(1, history.ResultingInPreparationQuantity);
        Assert.Equal(3, history.ResultingReadyQuantity);

        var command = (await fixture.ReadPreparationCommandsAsync(token))
            .Single(item => item.ResultHistoryId == result.HistoryId);
        Assert.Equal(PreparationCommand.MarkQuantityReadyCommandKind, command.CommandKind);
        Assert.Equal(2, command.Quantity);
        Assert.Equal(scenario.Actor.IdentityId, command.ActorIdentityId);
    }

    [Fact]
    public async Task Last_ready_quantity_derives_fully_ready_and_new_key_is_conflict()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 5, token);
        using var client = scenario.Client;

        using var response = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, Guid.NewGuid(), 5, token);
        var result = await PreparationReadyTestSupport.ReadSuccessAsync(response, token);
        using var again = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, Guid.NewGuid(), 1, token);

        Assert.Equal(5, result.ReadyQuantity);
        Assert.Equal(0, result.PendingQuantity);
        Assert.Equal(0, result.InPreparationQuantity);
        var readyHistory = (await fixture.ReadPreparationHistoryAsync(token))
            .Single(item => item.Id == result.HistoryId);
        Assert.Equal(PreparationHistory.QuantityReadyEventKind, readyHistory.EventKind);
        Assert.Equal(5, readyHistory.Quantity);
        Assert.Equal(5, readyHistory.ResultingTotalQuantity);
        Assert.Equal(0, readyHistory.ResultingPendingQuantity);
        Assert.Equal(0, readyHistory.ResultingInPreparationQuantity);
        Assert.Equal(5, readyHistory.ResultingReadyQuantity);
        await PreparationReadyTestSupport.AssertProblemAsync(
            again,
            HttpStatusCode.Conflict,
            "order_operations.preparation_ready.in_preparation_quantity_insufficient",
            token);
        Assert.Equal(2, (await fixture.ReadPreparationHistoryAsync(token)).Count);
        Assert.Equal(2, (await fixture.ReadPreparationCommandsAsync(token)).Count);
    }

    [Fact]
    public async Task Ready_cannot_consume_pending_quantity()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 0, token);
        using var client = scenario.Client;

        using var response = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, Guid.NewGuid(), 1, token);

        await PreparationReadyTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.preparation_ready.in_preparation_quantity_insufficient",
            token);
        await AssertCountersAsync(5, 0, 0, token);
        Assert.Empty(await fixture.ReadPreparationHistoryAsync(token));
        Assert.Empty(await fixture.ReadPreparationCommandsAsync(token));
    }

    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        using var response = await PreparationReadyTestSupport.PostAsync(
            fixture.Client, Guid.CreateVersion7(), Guid.NewGuid(), 1, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unusable_session_or_inactive_identity_is_unauthorized(
        bool deactivateIdentity)
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var client = scenario.Client;
        if (deactivateIdentity)
        {
            await fixture.SetIdentityActiveAsync(scenario.Actor.IdentityId, false, token);
        }
        else
        {
            await fixture.RevokeSessionsAsync(scenario.Actor.IdentityId, token);
        }

        using var response = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, Guid.NewGuid(), 1, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertCountersAsync(3, 2, 0, token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_quantity_is_bad_request_without_ready_effect(int quantity)
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var client = scenario.Client;

        using var response = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, Guid.NewGuid(), quantity, token);

        await PreparationReadyTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.preparation_ready.quantity_invalid",
            token);
        await AssertCountersAsync(3, 2, 0, token);
        Assert.Single(await fixture.ReadPreparationHistoryAsync(token));
    }

    [Fact]
    public async Task Insufficient_in_preparation_is_conflict_without_clipping()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var client = scenario.Client;

        using var response = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, Guid.NewGuid(), 3, token);

        await PreparationReadyTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.preparation_ready.in_preparation_quantity_insufficient",
            token);
        await AssertCountersAsync(3, 2, 0, token);
    }

    [Fact]
    public async Task Another_authorized_identity_can_mark_quantity_started_by_first_actor_ready()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var firstClient = scenario.Client;
        var secondActor = await fixture.CreatePreparationActorAsync(
            true, scenario.ResponsibilityId, token);
        using var secondClient = await fixture.LoginAsync(secondActor, token);

        using var response = await PreparationReadyTestSupport.PostAsync(
            secondClient, scenario.Work.Id, Guid.NewGuid(), 2, token);
        await PreparationReadyTestSupport.ReadSuccessAsync(response, token);

        await AssertCountersAsync(3, 0, 2, token);
        var history = await fixture.ReadPreparationHistoryAsync(token);
        Assert.Equal(scenario.Actor.IdentityId, history[0].ActorIdentityId);
        Assert.Equal(PreparationHistory.QuantityStartedEventKind, history[0].EventKind);
        Assert.Equal(secondActor.IdentityId, history[1].ActorIdentityId);
        Assert.Equal(PreparationHistory.QuantityReadyEventKind, history[1].EventKind);
    }

    [Fact]
    public async Task Missing_preparation_is_forbidden_before_work_lookup()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var starterClient = scenario.Client;
        var actor = await fixture.CreatePreparationActorAsync(false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var existing = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, Guid.NewGuid(), 1, token);
        using var absent = await PreparationReadyTestSupport.PostAsync(
            client, Guid.CreateVersion7(), Guid.NewGuid(), 1, token);

        await PreparationReadyTestSupport.AssertProblemAsync(
            existing, HttpStatusCode.Forbidden,
            "order_operations.preparation.forbidden", token);
        await PreparationReadyTestSupport.AssertProblemAsync(
            absent, HttpStatusCode.Forbidden,
            "order_operations.preparation.forbidden", token);
    }

    [Fact]
    public async Task Missing_enablement_and_absent_work_are_indistinguishable()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var starterClient = scenario.Client;
        var actor = await fixture.CreatePreparationActorAsync(true, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var hidden = await PreparationReadyTestSupport.PostAsync(
            client, scenario.Work.Id, Guid.NewGuid(), 1, token);
        using var absent = await PreparationReadyTestSupport.PostAsync(
            client, Guid.CreateVersion7(), Guid.NewGuid(), 1, token);

        await PreparationReadyTestSupport.AssertProblemAsync(
            hidden, HttpStatusCode.NotFound,
            "order_operations.preparation_work.not_found", token);
        await PreparationReadyTestSupport.AssertProblemAsync(
            absent, HttpStatusCode.NotFound,
            "order_operations.preparation_work.not_found", token);
    }

    [Theory]
    [InlineData("actorIdentityId")]
    [InlineData("preparationResponsibilityId")]
    [InlineData("completed")]
    [InlineData("delivered")]
    public async Task Unknown_authority_or_future_state_fields_are_rejected(string field)
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var client = scenario.Client;
        var json = $$"""{ "quantity": 1, "{{field}}": true }""";

        using var response = await PreparationReadyTestSupport.PostRawAsync(
            client, scenario.Work.Id, Guid.NewGuid(), json, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertCountersAsync(3, 2, 0, token);
    }

    [Fact]
    public async Task Malformed_json_is_bad_request_without_ready_effect()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var client = scenario.Client;

        using var response = await PreparationReadyTestSupport.PostRawAsync(
            client, scenario.Work.Id, Guid.NewGuid(), "{ not-json", token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertCountersAsync(3, 2, 0, token);
    }

    [Theory]
    [InlineData(null, "order_operations.preparation_ready.idempotency_key_required")]
    [InlineData("bad", "order_operations.preparation_ready.idempotency_key_invalid")]
    [InlineData("018f4f3c-5e34-7abc-8def-1234567890ab", "order_operations.preparation_ready.idempotency_key_invalid")]
    public async Task Missing_or_invalid_idempotency_key_is_bad_request(
        string? key,
        string code)
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var client = scenario.Client;

        using var response = await PreparationReadyTestSupport.PostWithRawIdentifiersAsync(
            client, scenario.Work.Id.ToString("D"), key, 1, token);

        await PreparationReadyTestSupport.AssertProblemAsync(
            response, HttpStatusCode.BadRequest, code, token);
    }

    [Fact]
    public async Task Malformed_work_id_is_bad_request()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var client = scenario.Client;

        using var response = await PreparationReadyTestSupport.PostWithRawIdentifiersAsync(
            client, "bad", Guid.NewGuid().ToString("D"), 1, token);

        await PreparationReadyTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.preparation_ready.work_id_invalid",
            token);
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_bad_request()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var client = scenario.Client;
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/preparation/work/{scenario.Work.Id:D}/ready")
        {
            Content = JsonContent.Create(new MarkPreparationQuantityReadyRequest(1))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));

        using var response = await client.SendAsync(request, token);

        await PreparationReadyTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.preparation_ready.antiforgery_invalid",
            token);
        await AssertCountersAsync(3, 2, 0, token);
    }

    [Fact]
    public async Task Command_failure_rolls_back_ready_history_and_command()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var client = scenario.Client;
        await fixture.SetPreparationCommandFailureAsync(true, token);
        try
        {
            using var response = await PreparationReadyTestSupport.PostAsync(
                client, scenario.Work.Id, Guid.NewGuid(), 2, token);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await AssertCountersAsync(3, 2, 0, token);
            Assert.Single(await fixture.ReadPreparationHistoryAsync(token));
            Assert.Single(await fixture.ReadPreparationCommandsAsync(token));
        }
        finally
        {
            await fixture.SetPreparationCommandFailureAsync(false, token);
        }
    }

    [Fact]
    public async Task History_failure_rolls_back_work_and_command()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateStartedScenarioAsync(5, 2, token);
        using var client = scenario.Client;
        await fixture.SetPreparationHistoryFailureAsync(true, token);
        try
        {
            using var response = await PreparationReadyTestSupport.PostAsync(
                client, scenario.Work.Id, Guid.NewGuid(), 2, token);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await AssertCountersAsync(3, 2, 0, token);
            Assert.Single(await fixture.ReadPreparationHistoryAsync(token));
            Assert.Single(await fixture.ReadPreparationCommandsAsync(token));
        }
        finally
        {
            await fixture.SetPreparationHistoryFailureAsync(false, token);
        }
    }

    [Fact]
    public async Task OpenApi_describes_ready_contract()
    {
        var token = TestContext.Current.CancellationToken;
        using var response = await fixture.Client.GetAsync("/openapi/v1.json", token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(token),
            cancellationToken: token);
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/order-operations/preparation/work/{workId}/ready")
            .GetProperty("post");

        foreach (var status in new[] { "200", "400", "401", "403", "404", "409" })
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        }
    }

    private async Task<ReadyScenario> CreateStartedScenarioAsync(
        int total,
        int started,
        CancellationToken token)
    {
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token, quantity: total);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        var actor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        var client = await fixture.LoginAsync(actor, token);
        if (started > 0)
        {
            using var response = await PreparationStartTestSupport.PostAsync(
                client, work.Id, Guid.NewGuid(), started, token);
            await PreparationStartTestSupport.ReadSuccessAsync(response, token);
        }

        return new ReadyScenario(responsibility, actor, client, work);
    }

    private async Task AssertCountersAsync(
        int pending,
        int inPreparation,
        int ready,
        CancellationToken token)
    {
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(pending, work.PendingQuantity);
        Assert.Equal(inPreparation, work.InPreparationQuantity);
        Assert.Equal(ready, work.ReadyQuantity);
        Assert.Equal(work.TotalQuantity, pending + inPreparation + ready);
    }

    private sealed record ReadyScenario(
        Guid ResponsibilityId,
        PreparationActor Actor,
        HttpClient Client,
        PreparationWorkSnapshot Work);
}
