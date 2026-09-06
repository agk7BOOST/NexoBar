using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class DeliveryCorrectionApiTests(OrderOperationsApiFixture fixture)
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    public async Task Correction_retracts_exact_effective_quantity_and_preserves_other_state(bool prepared, int quantity)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = prepared ? await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 3, 0, token)
            : await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        if (prepared)
        {
            var initialWork = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
            var actor = await fixture.CreatePreparationActorAsync(true, initialWork.PreparationResponsibilityId, token);
            using var preparer = await fixture.LoginAsync(actor, token);
            using var start = await PreparationStartTestSupport.PostAsync(preparer, initialWork.Id, Guid.NewGuid(), 3, token);
            start.EnsureSuccessStatusCode();
            using var ready = await PreparationReadyTestSupport.PostAsync(preparer, initialWork.Id, Guid.NewGuid(), 3, token);
            ready.EnsureSuccessStatusCode();
        }
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 3, token);
        var contents = await fixture.ReadConfirmedContentsAsync(token);
        var work = await fixture.ReadPreparationWorkAsync(token);
        var preparationHistory = (await fixture.ReadPreparationHistoryAsync(token)).Select(x =>
            (x.Id, x.WorkId, x.EventKind, x.Quantity, x.ActorIdentityId, x.OccurredAt, x.ResultingTotalQuantity,
             x.ResultingPendingQuantity, x.ResultingInPreparationQuantity, x.ResultingReadyQuantity)).ToArray();
        var originalHistory = (await fixture.ReadDeliveryHistoryAsync(token)).Select(x =>
            (x.Id, x.IncorporationId, x.ContentOrdinal, x.EventKind, x.Quantity, x.ActorIdentityId, x.OccurredAt, x.ResultingDeliveredQuantity)).ToArray();
        var before = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, token);
        Assert.True(before.IsLiquidationEligible);
        var earliest = DateTimeOffset.UtcNow.AddSeconds(-1);
        using var response = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), quantity, token);
        var result = await DeliveryCorrectionTestSupport.SuccessAsync(response, token);
        Assert.Equal(Guid.Parse(target.OperationalReference), result.OrderId);
        Assert.Equal(target.IncorporationId, result.IncorporationId);
        Assert.Equal(target.ContentOrdinal, result.ContentOrdinal);
        Assert.Equal(quantity, result.CorrectedQuantity);
        Assert.Equal(3, result.PreviousDeliveredQuantity);
        Assert.Equal(3 - quantity, result.ResultingDeliveredQuantity);
        Assert.Equal(TimeSpan.Zero, result.OccurredAt.Offset);
        Assert.InRange(result.OccurredAt, earliest, DateTimeOffset.UtcNow);
        Assert.Equal(0, result.OccurredAt.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.Equal(3 - quantity, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
        Assert.Equal(contents, await fixture.ReadConfirmedContentsAsync(token));
        Assert.Equal(work, await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(preparationHistory, (await fixture.ReadPreparationHistoryAsync(token)).Select(x =>
            (x.Id, x.WorkId, x.EventKind, x.Quantity, x.ActorIdentityId, x.OccurredAt, x.ResultingTotalQuantity,
             x.ResultingPendingQuantity, x.ResultingInPreparationQuantity, x.ResultingReadyQuantity)).ToArray());
        Assert.Equal(originalHistory, (await fixture.ReadDeliveryHistoryAsync(token)).Select(x =>
            (x.Id, x.IncorporationId, x.ContentOrdinal, x.EventKind, x.Quantity, x.ActorIdentityId, x.OccurredAt, x.ResultingDeliveredQuantity)).ToArray());
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var history = await db.DeliveryCorrectionHistory.SingleAsync(token);
        Assert.Equal("DeliveryQuantityCorrected", history.EventKind);
        Assert.Equal(fixture.DefaultOrderOperationsActor.IdentityId, history.ActorIdentityId);
        Assert.Equal(result, new DeliveryCorrectionResponse(history.OrderId, history.IncorporationId, history.ContentOrdinal,
            history.Id, history.CorrectedQuantity, history.PreviousDeliveredQuantity, history.ResultingDeliveredQuantity, history.OccurredAt));
        await DeliveryCorrectionTestSupport.CountsAsync(fixture, 1, token);
        var after = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, token);
        var price = decimal.Parse(before.Incorporations.Single().Items.Single().AppliedPrice, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(before.Incorporations.Single().Items, after.Incorporations.Single().Items);
        Assert.Equal((3 - quantity) * price, decimal.Parse(after.FunctionalAmount, System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(after.IsLiquidationEligible);
        Assert.Contains("unresolved_fulfillment", after.LiquidationBlockers);
        using var deliveryRead = await fixture.OrderOperationsClient.GetAsync($"/api/order-operations/orders/{target.OperationalReference}/delivery", token);
        using var read = JsonDocument.Parse(await deliveryRead.Content.ReadAsStringAsync(token));
        var content = read.RootElement.GetProperty("contents")[0];
        Assert.Equal(quantity, content.GetProperty("deliverableQuantity").GetInt32());
        Assert.Equal(quantity, content.GetProperty("remainingQuantity").GetInt32());
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, quantity, token);
        Assert.True((await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, token)).IsLiquidationEligible);
    }

    [Theory]
    [InlineData(0, 1, 409, "no_effective_delivery")]
    [InlineData(2, 3, 409, "quantity_exceeds_delivered")]
    [InlineData(2, int.MaxValue, 409, "quantity_exceeds_delivered")]
    [InlineData(2, 0, 400, "quantity_invalid")]
    [InlineData(2, -1, 400, "quantity_invalid")]
    public async Task Invalid_quantity_never_clamps_or_changes_state(int delivered, int quantity, int status, string code)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        if (delivered > 0) await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, delivered, token);
        using var response = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), quantity, token);
        await DeliveryCorrectionTestSupport.ProblemAsync(response, (HttpStatusCode)status, code, token);
        Assert.Equal(delivered, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
        await DeliveryCorrectionTestSupport.CountsAsync(fixture, 0, token);
    }

    [Theory]
    [InlineData("{\"quantity\":1,\"actorIdentityId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"quantity\":1.5}")]
    [InlineData("{\"quantity\":2147483648}")]
    [InlineData("{\"quantity\":")]
    public async Task Malformed_body_and_client_actor_are_rejected(string body)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        using var request = new HttpRequestMessage(HttpMethod.Post, DeliveryCorrectionTestSupport.Path(target))
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, token);
        await DeliveryCorrectionTestSupport.ProblemAsync(response, HttpStatusCode.BadRequest, "request_invalid", token);
        await DeliveryCorrectionTestSupport.CountsAsync(fixture, 0, token);
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("wrong-key-version")]
    [InlineData("antiforgery")]
    [InlineData("target")]
    public async Task Structural_guards_return_problem_details(string kind)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        if (kind == "target") target = target with { OperationalReference = "invalid" };
        using var request = new HttpRequestMessage(HttpMethod.Post, DeliveryCorrectionTestSupport.Path(target)) { Content = JsonContent.Create(new CorrectDeliveryRequest(1)) };
        if (kind != "missing-key") request.Headers.Add("Idempotency-Key", (kind == "wrong-key-version" ? Guid.CreateVersion7() : Guid.NewGuid()).ToString());
        using var response = kind == "antiforgery" ? await fixture.OrderOperationsClient.SendAsync(request, token)
            : await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, token);
        var code = kind switch { "missing-key" => "idempotency_key_required", "wrong-key-version" => "idempotency_key_invalid", "antiforgery" => "antiforgery_invalid", _ => "target_invalid" };
        await DeliveryCorrectionTestSupport.ProblemAsync(response, HttpStatusCode.BadRequest, code, token);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("incorporation")]
    [InlineData("ordinal")]
    [InlineData("wrong-order")]
    public async Task Exact_target_must_exist_in_order(string kind)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        if (kind == "order") target = target with { OperationalReference = Guid.CreateVersion7().ToString() };
        if (kind == "incorporation") target = target with { IncorporationId = Guid.CreateVersion7() };
        if (kind == "ordinal") target = target with { ContentOrdinal = 99 };
        if (kind == "wrong-order")
        {
            var other = await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("5", 1)], token);
            target = target with { OperationalReference = other.OperationalReference };
        }
        using var response = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        await DeliveryCorrectionTestSupport.ProblemAsync(response, HttpStatusCode.NotFound, "content_not_found", token);
        await DeliveryCorrectionTestSupport.CountsAsync(fixture, 0, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_and_closed_orders_reject_correction(bool close)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 3, token);
        using var liquidation = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), token);
        await LiquidationTestSupport.ReadSuccessAsync(liquidation, token);
        if (close)
        {
            using var closure = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), token);
            await ClosureTestSupport.ReadSuccessAsync(closure, token);
        }
        using var response = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        await DeliveryQuantityTestSupport.AssertProblemAsync(response, HttpStatusCode.Conflict, "order_operations.order.frozen", token);
        Assert.Equal(3, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
        await DeliveryCorrectionTestSupport.CountsAsync(fixture, 0, token);
    }

    [Theory]
    [InlineData("history")]
    [InlineData("commands")]
    public async Task Failure_rolls_back_state_history_and_command(string suffix)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 3, token);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var sql = connection.CreateCommand();
        sql.CommandText = $"""
            CREATE FUNCTION order_operations.fail_correction() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'controlled correction failure'; END; $$;
            CREATE TRIGGER fail_correction BEFORE INSERT ON order_operations.delivery_correction_{suffix}
            FOR EACH ROW EXECUTE FUNCTION order_operations.fail_correction();
            """;
        await sql.ExecuteNonQueryAsync(token);
        var key = Guid.NewGuid();
        try
        {
            using var response = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 1, token);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(3, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
            Assert.Single(await fixture.ReadDeliveryHistoryAsync(token));
            await DeliveryCorrectionTestSupport.CountsAsync(fixture, 0, token);
        }
        finally
        {
            sql.CommandText = $"DROP TRIGGER fail_correction ON order_operations.delivery_correction_{suffix}; DROP FUNCTION order_operations.fail_correction();";
            await sql.ExecuteNonQueryAsync(token);
        }
        using var retry = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 1, token);
        await DeliveryCorrectionTestSupport.SuccessAsync(retry, token);
    }
}
