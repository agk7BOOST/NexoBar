using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class DeliveryQuantityApiTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Direct_delivery_is_partial_exact_and_does_not_create_work()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 5, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var firstResponse = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 2, token);
        var first = await DeliveryQuantityTestSupport.ReadSuccessAsync(firstResponse, token);
        using var secondResponse = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 3, token);
        var second = await DeliveryQuantityTestSupport.ReadSuccessAsync(secondResponse, token);

        Assert.Equal(2, first.DeliveredQuantity);
        Assert.Equal(5, second.DeliveredQuantity);
        Assert.Equal(5, Assert.Single(await fixture.ReadDeliveryStatesAsync(token))
            .DeliveredQuantity);
        Assert.Empty(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(2, (await fixture.ReadDeliveryHistoryAsync(token)).Count);

        using var fullyDelivered = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 1, token);
        await DeliveryQuantityTestSupport.AssertProblemAsync(
            fullyDelivered,
            HttpStatusCode.Conflict,
            "order_operations.delivery.deliverable_quantity_insufficient",
            token);
    }

    [Fact]
    public async Task Direct_delivery_does_not_clip_an_excess_request()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 5, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 6, token);

        await AssertConflictWithoutDeliveryEffectsAsync(response, 0, token);
    }

    [Fact]
    public async Task Prepared_delivery_uses_ready_without_consuming_it()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(
            fixture, 5, ready: 2, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var firstResponse = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 1, token);
        var first = await DeliveryQuantityTestSupport.ReadSuccessAsync(firstResponse, token);
        using var secondResponse = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 1, token);
        var second = await DeliveryQuantityTestSupport.ReadSuccessAsync(secondResponse, token);

        Assert.Equal(1, first.DeliveredQuantity);
        Assert.Equal(2, second.DeliveredQuantity);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(2, work.ReadyQuantity);
        Assert.Equal(3, work.PendingQuantity);
        Assert.Equal(0, work.InPreparationQuantity);

        using var unavailable = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 1, token);
        await DeliveryQuantityTestSupport.AssertProblemAsync(
            unavailable,
            HttpStatusCode.Conflict,
            "order_operations.delivery.deliverable_quantity_insufficient",
            token);

        await fixture.SetPreparationQuantitiesAsync(work.Id, 2, 0, 3, token);
        using var afterReady = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 1, token);
        var progressed = await DeliveryQuantityTestSupport.ReadSuccessAsync(afterReady, token);
        Assert.Equal(3, progressed.DeliveredQuantity);
        Assert.Equal(3, Assert.Single(await fixture.ReadPreparationWorkAsync(token))
            .ReadyQuantity);
        var history = await fixture.ReadDeliveryHistoryAsync(token);
        Assert.Equal([1, 2, 3], history
            .Select(entry => entry.ResultingDeliveredQuantity)
            .ToArray());
        Assert.All(history, entry =>
        {
            Assert.Equal(DeliveryHistory.QuantityDeliveredEventKind, entry.EventKind);
            Assert.Equal(1, entry.Quantity);
            Assert.Equal(actor.IdentityId, entry.ActorIdentityId);
        });
    }

    [Fact]
    public async Task Prepared_delivery_before_any_ready_is_conflict()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(
            fixture, 5, ready: 0, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 1, token);

        await AssertConflictWithoutDeliveryEffectsAsync(response, 0, token);
        Assert.Equal(0, Assert.Single(await fixture.ReadPreparationWorkAsync(token))
            .ReadyQuantity);
    }

    [Fact]
    public async Task Partial_prepared_delivery_is_reflected_by_authoritative_query()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(
            fixture, 5, ready: 3, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);
        using var response = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 2, token);
        await DeliveryQuantityTestSupport.ReadSuccessAsync(response, token);

        using var queryResponse = await client.GetAsync(
            $"/api/order-operations/orders/{target.OperationalReference}/delivery",
            token);
        queryResponse.EnsureSuccessStatusCode();
        var query = Assert.IsType<OrderDeliveryResponse>(
            await queryResponse.Content.ReadFromJsonAsync<OrderDeliveryResponse>(token));
        var content = Assert.Single(query.Contents);

        Assert.Equal(5, content.TotalQuantity);
        Assert.Equal(3, content.ReadyQuantity);
        Assert.Equal(2, content.DeliveredQuantity);
        Assert.Equal(1, content.DeliverableQuantity);
        Assert.Equal(3, content.RemainingQuantity);
    }

    [Fact]
    public async Task Success_persists_one_semantic_history_with_actor_and_exact_result()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 5, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        var expected = new DateTimeOffset(2026, 8, 31, 18, 20, 30, TimeSpan.Zero);
        await using var application = fixture.CreateApplicationWithTimeProvider(
            new FixedTimeProvider(expected));
        using var client = await fixture.LoginAsync(actor, token, application);
        var key = Guid.NewGuid();

        using var response = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, key, 2, token);
        var result = await DeliveryQuantityTestSupport.ReadSuccessAsync(response, token);

        Assert.Equal(target.IncorporationId, result.IncorporationId);
        Assert.Equal(target.ContentOrdinal, result.ContentOrdinal);
        Assert.Equal(7, result.HistoryId.ToByteArray(bigEndian: true)[6] >> 4);
        Assert.Equal(expected, result.OccurredAt);
        Assert.Equal(2, result.DeliveredQuantity);
        var history = Assert.Single(await fixture.ReadDeliveryHistoryAsync(token));
        Assert.Equal(DeliveryHistory.QuantityDeliveredEventKind, history.EventKind);
        Assert.Equal(2, history.Quantity);
        Assert.Equal(actor.IdentityId, history.ActorIdentityId);
        Assert.Equal(expected, history.OccurredAt);
        Assert.Equal(2, history.ResultingDeliveredQuantity);
        var command = Assert.Single(await fixture.ReadDeliveryCommandsAsync(token));
        Assert.Equal(key, command.IdempotencyKey);
        Assert.Equal(actor.IdentityId, command.ActorIdentityId);
        Assert.Equal(DeliveryCommand.DeliverQuantityCommandKind, command.CommandKind);
        Assert.Equal(target.IncorporationId, command.IncorporationId);
        Assert.Equal(target.ContentOrdinal, command.ContentOrdinal);
        Assert.Equal(2, command.Quantity);
        Assert.Equal(result.HistoryId, command.ResultHistoryId);
    }

    [Fact]
    public async Task Order_operations_alone_authorizes_delivery_of_prepared_content()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(
            fixture, 2, ready: 1, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 1, token);

        await DeliveryQuantityTestSupport.ReadSuccessAsync(response, token);
    }

    [Fact]
    public async Task Same_product_contents_keep_independent_delivery_state_and_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua repetida", "3", token);
        var responsibilityId = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(product.Id, responsibilityId, token);
        using (var request = new HttpRequestMessage(
                   HttpMethod.Post,
                   "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                       "Mesa repetida",
                       [
                           new FirstConfirmationItemRequest(product.Id, 2),
                           new FirstConfirmationItemRequest(product.Id, 2, "con hielo")
                       ]))
        })
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            using var confirmation = await fixture.Client.SendAsync(request, token);
            confirmation.EnsureSuccessStatusCode();
        }

        var contents = await fixture.ReadConfirmedContentsAsync(token);
        Assert.Equal(2, contents.Count);
        foreach (var work in await fixture.ReadPreparationWorkAsync(token))
        {
            await fixture.SetPreparationQuantitiesAsync(work.Id, 1, 0, 1, token);
        }

        var target = contents[0];
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await DeliveryQuantityTestSupport.PostAsync(
            client,
            target.IncorporationId,
            target.ContentOrdinal,
            Guid.NewGuid(),
            1,
            token);
        await DeliveryQuantityTestSupport.ReadSuccessAsync(response, token);

        var states = await fixture.ReadDeliveryStatesAsync(token);
        Assert.Equal(1, states.Single(state =>
            state.IncorporationId == target.IncorporationId &&
            state.ContentOrdinal == target.ContentOrdinal).DeliveredQuantity);
        Assert.Equal(0, states.Single(state =>
            state.IncorporationId == contents[1].IncorporationId &&
            state.ContentOrdinal == contents[1].ContentOrdinal).DeliveredQuantity);
        var history = Assert.Single(await fixture.ReadDeliveryHistoryAsync(token));
        Assert.Equal(target.IncorporationId, history.IncorporationId);
        Assert.Equal(target.ContentOrdinal, history.ContentOrdinal);
    }

    [Fact]
    public async Task New_delivery_without_order_operations_is_forbidden()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, token);
        var actor = await fixture.CreateDeliveryActorAsync(false, true, Guid.CreateVersion7(), token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 1, token);

        await DeliveryQuantityTestSupport.AssertProblemAsync(
            response, HttpStatusCode.Forbidden, "order_operations.delivery.forbidden", token);
    }

    [Fact]
    public async Task Anonymous_delivery_is_unauthorized()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        using var response = await DeliveryQuantityTestSupport.PostAsync(
            fixture.Client, Guid.CreateVersion7(), 1, Guid.NewGuid(), 1, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_quantity_is_bad_request(int quantity)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), quantity, token);

        await DeliveryQuantityTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.delivery.quantity_invalid",
            token);
    }

    [Theory]
    [InlineData("not-a-uuid", "1", "order_operations.delivery.incorporation_id_invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000", "0", "order_operations.delivery.content_ordinal_invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000", "-1", "order_operations.delivery.content_ordinal_invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000", "one", "order_operations.delivery.content_ordinal_invalid")]
    public async Task Invalid_target_is_bad_request(
        string incorporationId,
        string ordinal,
        string code)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await DeliveryQuantityTestSupport.PostWithRawTargetAsync(
            client,
            incorporationId,
            ordinal,
            Guid.NewGuid().ToString("D"),
            1,
            includeAntiforgery: true,
            token);

        await DeliveryQuantityTestSupport.AssertProblemAsync(
            response, HttpStatusCode.BadRequest, code, token);
    }

    [Theory]
    [InlineData(null, "order_operations.delivery.idempotency_key_required")]
    [InlineData("not-a-uuid", "order_operations.delivery.idempotency_key_invalid")]
    [InlineData("018f4f3c-5e34-7abc-8def-1234567890ab", "order_operations.delivery.idempotency_key_invalid")]
    public async Task Missing_or_invalid_key_is_bad_request(string? key, string code)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await DeliveryQuantityTestSupport.PostWithRawTargetAsync(
            client,
            target.IncorporationId.ToString("D"),
            target.ContentOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            key,
            1,
            includeAntiforgery: true,
            token);

        await DeliveryQuantityTestSupport.AssertProblemAsync(
            response, HttpStatusCode.BadRequest, code, token);
    }

    [Fact]
    public async Task Unknown_request_properties_are_rejected()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await DeliveryQuantityTestSupport.PostRawAsync(
            client,
            target.IncorporationId,
            target.ContentOrdinal,
            Guid.NewGuid(),
            "{\"quantity\":1,\"actor\":\"client\"}",
            token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_antiforgery_is_bad_request()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await DeliveryQuantityTestSupport.PostWithRawTargetAsync(
            client,
            target.IncorporationId.ToString("D"),
            target.ContentOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Guid.NewGuid().ToString("D"),
            1,
            includeAntiforgery: false,
            token);

        await DeliveryQuantityTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.delivery.antiforgery_invalid",
            token);
    }

    [Fact]
    public async Task Missing_content_is_not_found()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);

        using var response = await DeliveryQuantityTestSupport.PostAsync(
            client, Guid.CreateVersion7(), 1, Guid.NewGuid(), 1, token);

        await DeliveryQuantityTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            "order_operations.delivery.content_not_found",
            token);
    }

    [Theory]
    [InlineData(DeliveryMutationCorruption.PreparedWithoutWork)]
    [InlineData(DeliveryMutationCorruption.DirectWithWork)]
    [InlineData(DeliveryMutationCorruption.MissingDeliveryState)]
    [InlineData(DeliveryMutationCorruption.DeliveredAboveTotal)]
    [InlineData(DeliveryMutationCorruption.PreparedDeliveredAboveReady)]
    [InlineData(DeliveryMutationCorruption.PreparedWorkTotalMismatch)]
    public async Task Corrupt_state_is_rejected_without_delivery_effects(
        DeliveryMutationCorruption corruption)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var prepared = corruption is
            DeliveryMutationCorruption.PreparedWithoutWork or
            DeliveryMutationCorruption.PreparedDeliveredAboveReady or
            DeliveryMutationCorruption.PreparedWorkTotalMismatch;
        var target = prepared
            ? await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 5, 2, token)
            : await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 5, token);
        switch (corruption)
        {
            case DeliveryMutationCorruption.PreparedWithoutWork:
                await fixture.DeletePreparationWorkAsync(
                    target.IncorporationId, target.ContentOrdinal, token);
                break;
            case DeliveryMutationCorruption.DirectWithWork:
                await fixture.AddContradictoryPreparationWorkAsync(
                    target.IncorporationId, target.ContentOrdinal, 5, token);
                break;
            case DeliveryMutationCorruption.MissingDeliveryState:
                await fixture.DeleteDeliveryStateAsync(
                    target.IncorporationId, target.ContentOrdinal, token);
                break;
            case DeliveryMutationCorruption.DeliveredAboveTotal:
                await fixture.SetDeliveredQuantityAsync(
                    target.IncorporationId, target.ContentOrdinal, 6, token);
                break;
            case DeliveryMutationCorruption.PreparedDeliveredAboveReady:
                await fixture.SetDeliveredQuantityAsync(
                    target.IncorporationId, target.ContentOrdinal, 3, token);
                break;
            case DeliveryMutationCorruption.PreparedWorkTotalMismatch:
                await fixture.SetPreparationSnapshotAsync(
                    target.WorkId!.Value, 6, 4, 0, 2, token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);
        using var response = await DeliveryQuantityTestSupport.PostAsync(
            client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 1, token);

        await DeliveryQuantityTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "order_operations.delivery.state_inconsistent",
            token);
        Assert.Empty(await fixture.ReadDeliveryHistoryAsync(token));
        Assert.Empty(await fixture.ReadDeliveryCommandsAsync(token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Persistence_failure_rolls_back_state_history_and_command(
        bool failHistory)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 5, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var client = await fixture.LoginAsync(actor, token);
        if (failHistory)
        {
            await fixture.SetDeliveryHistoryFailureAsync(true, token);
        }
        else
        {
            await fixture.SetDeliveryCommandFailureAsync(true, token);
        }

        try
        {
            using var response = await DeliveryQuantityTestSupport.PostAsync(
                client, target.IncorporationId, target.ContentOrdinal, Guid.NewGuid(), 2, token);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(0, Assert.Single(await fixture.ReadDeliveryStatesAsync(token))
                .DeliveredQuantity);
            Assert.Empty(await fixture.ReadDeliveryHistoryAsync(token));
            Assert.Empty(await fixture.ReadDeliveryCommandsAsync(token));
        }
        finally
        {
            if (failHistory)
            {
                await fixture.SetDeliveryHistoryFailureAsync(false, token);
            }
            else
            {
                await fixture.SetDeliveryCommandFailureAsync(false, token);
            }
        }
    }

    [Fact]
    public async Task OpenApi_describes_delivery_contract()
    {
        var token = TestContext.Current.CancellationToken;
        using var response = await fixture.Client.GetAsync("/openapi/v1.json", token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(token),
            cancellationToken: token);
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty(
                "/api/order-operations/incorporations/{incorporationId}/contents/{contentOrdinal}/deliver")
            .GetProperty("post");

        foreach (var status in new[] { "200", "400", "401", "403", "404", "409", "500" })
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        }
    }

    private async Task AssertConflictWithoutDeliveryEffectsAsync(
        HttpResponseMessage response,
        int delivered,
        CancellationToken token)
    {
        await DeliveryQuantityTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.delivery.deliverable_quantity_insufficient",
            token);
        Assert.Equal(delivered, Assert.Single(await fixture.ReadDeliveryStatesAsync(token))
            .DeliveredQuantity);
        Assert.Empty(await fixture.ReadDeliveryHistoryAsync(token));
        Assert.Empty(await fixture.ReadDeliveryCommandsAsync(token));
    }
}

public enum DeliveryMutationCorruption
{
    PreparedWithoutWork,
    DirectWithWork,
    MissingDeliveryState,
    DeliveredAboveTotal,
    PreparedDeliveredAboveReady,
    PreparedWorkTotalMismatch
}
