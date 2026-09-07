using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ContentCancellationApiTests(OrderOperationsApiFixture fixture)
{
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
        using var request = new HttpRequestMessage(HttpMethod.Post, ContentCancellationTestSupport.Path(target))
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, token);
        await ContentCancellationTestSupport.ProblemAsync(response, HttpStatusCode.BadRequest, "request_invalid", token);
        await ContentCancellationTestSupport.CountsAsync(fixture, 0, token);
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
        using var request = new HttpRequestMessage(HttpMethod.Post, ContentCancellationTestSupport.Path(target)) { Content = JsonContent.Create(new CancelContentRequest(1)) };
        if (kind != "missing-key") request.Headers.Add("Idempotency-Key", (kind == "wrong-key-version" ? Guid.CreateVersion7() : Guid.NewGuid()).ToString());
        using var response = kind == "antiforgery" ? await fixture.OrderOperationsClient.SendAsync(request, token)
            : await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, token);
        var code = kind switch { "missing-key" => "idempotency_key_required", "wrong-key-version" => "idempotency_key_invalid", "antiforgery" => "antiforgery_invalid", _ => "target_invalid" };
        await ContentCancellationTestSupport.ProblemAsync(response, HttpStatusCode.BadRequest, code, token);
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
        using var response = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        await ContentCancellationTestSupport.ProblemAsync(response, HttpStatusCode.NotFound, "content_not_found", token);
        await ContentCancellationTestSupport.CountsAsync(fixture, 0, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_and_closed_orders_reject_cancellation(bool close)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await ContentCancellationTestSupport.DeliverAsync(fixture, target, 3, token);
        using var liquidation = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), token);
        await LiquidationTestSupport.ReadSuccessAsync(liquidation, token);
        if (close)
        {
            using var closure = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), token);
            await ClosureTestSupport.ReadSuccessAsync(closure, token);
        }
        using var response = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        await DeliveryQuantityTestSupport.AssertProblemAsync(response, HttpStatusCode.Conflict, "order_operations.order.frozen", token);
        Assert.Equal(3, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
        await ContentCancellationTestSupport.CountsAsync(fixture, 0, token);
    }

    [Theory]
    [InlineData("history", false)]
    [InlineData("commands", false)]
    [InlineData("history", true)]
    [InlineData("commands", true)]
    public async Task Failure_rolls_back_state_history_and_command(string suffix, bool prepared)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = prepared ? await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 3, 0, token)
            : await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);

        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var sql = connection.CreateCommand();
        sql.CommandText = $"""
            CREATE FUNCTION order_operations.fail_cancellation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'controlled cancellation failure'; END; $$;
            CREATE TRIGGER fail_cancellation BEFORE INSERT ON order_operations.content_cancellation_{suffix}
            FOR EACH ROW EXECUTE FUNCTION order_operations.fail_cancellation();
            """;
        await sql.ExecuteNonQueryAsync(token);
        var key = Guid.NewGuid();
        try
        {
            using var response = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 1, token);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(0, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            Assert.Equal(0, (await db.ContentQuantityStates.SingleAsync(token)).CancelledQuantity);
            Assert.Empty(await fixture.ReadDeliveryHistoryAsync(token));
            if (prepared) Assert.Equal(3, Assert.Single(await fixture.ReadPreparationWorkAsync(token)).PendingQuantity);
            await ContentCancellationTestSupport.CountsAsync(fixture, 0, token);
        }
        finally
        {
            sql.CommandText = $"DROP TRIGGER fail_cancellation ON order_operations.content_cancellation_{suffix}; DROP FUNCTION order_operations.fail_cancellation();";
            await sql.ExecuteNonQueryAsync(token);
        }
        using var retry = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 1, token);
        await ContentCancellationTestSupport.SuccessAsync(retry, token);
    }
}
