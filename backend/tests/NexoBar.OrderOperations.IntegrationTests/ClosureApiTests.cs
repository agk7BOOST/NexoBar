using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ClosureApiTests(OrderOperationsApiFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_close_persists_state_history_actor_utc_and_keeps_exact_lookup(bool external)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token, external: external);
        var before = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, order.OperationalReference, token);
        Assert.False(before.IsClosed);
        Assert.Null(before.ClosedAt);
        Assert.True(before.IsClosureEligible);
        await ClosureTestSupport.AssertCountsAsync(fixture, 0, token);
        var key = Guid.NewGuid();
        using var response = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, key, token);
        var result = await ClosureTestSupport.ReadSuccessAsync(response, token);
        Assert.Equal(7, result.ClosureId.Version);
        Assert.Equal(Guid.Parse(order.OperationalReference), result.OrderId);
        Assert.True(result.IsClosed);
        Assert.Equal(TimeSpan.Zero, result.ClosedAt.Offset);
        Assert.Equal(0, result.ClosedAt.Ticks % TimeSpan.TicksPerMicrosecond);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var state = await db.Closures.SingleAsync(token);
        var history = await db.ClosureHistory.SingleAsync(token);
        var command = await db.ClosureCommands.SingleAsync(token);
        Assert.Equal(result.ClosureId, state.Id);
        Assert.Equal(result.OrderId, state.OrderId);
        Assert.Equal(result.ClosedAt, state.ClosedAt);
        Assert.Equal(fixture.DefaultOrderOperationsActor.IdentityId, state.ActorIdentityId);
        Assert.Equal("Closed", history.EventKind);
        Assert.Equal(state.Id, history.ClosureId);
        Assert.Equal(state.OrderId, history.OrderId);
        Assert.Equal(state.ClosedAt, history.OccurredAt);
        Assert.Equal(state.ActorIdentityId, history.ActorIdentityId);
        Assert.Equal(result, command.ToResponse());
        Assert.Equal(key, command.IdempotencyKey);
        Assert.Equal(state.ActorIdentityId, command.ActorIdentityId);
        Assert.Equal(ClosureCommand.CloseCommandKind, command.CommandKind);
        Assert.Single(await fixture.ReadLiquidationsAsync(token));
        Assert.Single(await fixture.ReadLiquidationHistoryAsync(token));
        var after = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, order.OperationalReference, token);
        Assert.True(after.IsClosed);
        Assert.Equal(result.ClosedAt, after.ClosedAt);
        Assert.False(after.IsClosureEligible);
        Assert.True(after.IsLiquidated);
        Assert.True(after.IsFrozen);
        Assert.Equal(before.LiquidatedAmount, after.LiquidatedAmount);
        Assert.Equal(before.LiquidationMode, after.LiquidationMode);
        Assert.Equal(before.DeclaredPaymentMedium, after.DeclaredPaymentMedium);
        Assert.Equal(before.Incorporations.Count, after.Incorporations.Count);
    }

    [Fact]
    public async Task Not_liquidated_is_conflict_and_not_eligible()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token, liquidate: false);
        using var response = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, Guid.NewGuid(), token);
        await ClosureTestSupport.AssertProblemAsync(response, HttpStatusCode.Conflict, "not_liquidated", token);
        var read = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, order.OperationalReference, token);
        Assert.False(read.IsClosureEligible);
        Assert.False(read.IsClosed);
        Assert.Null(read.ClosedAt);
        await ClosureTestSupport.AssertCountsAsync(fixture, 0, token);
    }

    [Fact]
    public async Task Replay_is_exact_and_durable_after_restart_and_revocation_but_new_key_is_forbidden()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token);
        var key = Guid.NewGuid();
        using var first = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, key, token);
        await ClosureTestSupport.ReadSuccessAsync(first, token);
        var originalJson = await first.Content.ReadAsStringAsync(token);
        await fixture.RestartApplicationAsync(token);
        await fixture.RevokeOrderOperationsAssignmentAsync(fixture.DefaultOrderOperationsActor.IdentityId, token);
        using var replay = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, key, token);
        await ClosureTestSupport.ReadSuccessAsync(replay, token);
        Assert.Equal(originalJson, await replay.Content.ReadAsStringAsync(token));
        using var newIntent = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, Guid.NewGuid(), token);
        await ClosureTestSupport.AssertProblemAsync(newIntent, HttpStatusCode.Forbidden, "forbidden", token);
        await ClosureTestSupport.AssertCountsAsync(fixture, 1, token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Inactive_identity_or_revoked_session_rejects_new_intent_and_replay(bool inactive, bool replay)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token);
        var key = Guid.NewGuid();
        if (replay)
        {
            using var original = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, key, token);
            await ClosureTestSupport.ReadSuccessAsync(original, token);
        }
        if (inactive)
            await fixture.SetIdentityActiveAsync(fixture.DefaultOrderOperationsActor.IdentityId, false, token);
        else
            await fixture.RevokeSessionsAsync(fixture.DefaultOrderOperationsActor.IdentityId, token);
        using var response = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, key, token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await ClosureTestSupport.AssertCountsAsync(fixture, replay ? 1 : 0, token);
    }

    [Fact]
    public async Task Anonymous_is_unauthorized_and_missing_order_is_not_found()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var orderId = Guid.CreateVersion7().ToString("D");
        using var anonymous = await ClosureTestSupport.PostAsync(fixture.Client, orderId, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var missing = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, orderId, Guid.NewGuid(), token);
        await ClosureTestSupport.AssertProblemAsync(missing, HttpStatusCode.NotFound, "order_not_found", token);
    }

    [Theory]
    [InlineData("order", "order_id_invalid")]
    [InlineData("missing_key", "idempotency_key_required")]
    [InlineData("key", "idempotency_key_invalid")]
    [InlineData("v7", "idempotency_key_invalid")]
    [InlineData("csrf", "antiforgery_invalid")]
    [InlineData("body", "body_not_allowed")]
    public async Task Malformed_requests_are_bad_requests(string scenario, string code)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var orderId = scenario == "order" ? "bad" : Guid.CreateVersion7().ToString("D");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{orderId}/close");
        if (scenario != "missing_key")
            request.Headers.Add("Idempotency-Key", scenario == "key" ? "bad" : scenario == "v7" ? Guid.CreateVersion7().ToString("D") : Guid.NewGuid().ToString("D"));
        if (scenario == "body")
            request.Content = JsonContent.Create(new { actorIdentityId = Guid.CreateVersion7() });
        using var response = scenario == "csrf"
            ? await fixture.OrderOperationsClient.SendAsync(request, token)
            : await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, token);
        await ClosureTestSupport.AssertProblemAsync(response, HttpStatusCode.BadRequest, code, token);
        await ClosureTestSupport.AssertCountsAsync(fixture, 0, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_key_with_different_actor_or_order_conflicts(bool differentActor)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token);
        var key = Guid.NewGuid();
        using var first = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, key, token);
        await ClosureTestSupport.ReadSuccessAsync(first, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var otherClient = await fixture.LoginAsync(actor, token);
        using var response = await ClosureTestSupport.PostAsync(
            differentActor ? otherClient : fixture.OrderOperationsClient,
            differentActor ? order.OperationalReference : Guid.CreateVersion7().ToString("D"), key, token);
        await ClosureTestSupport.AssertProblemAsync(response, HttpStatusCode.Conflict, "idempotency_key_conflict", token);
        await ClosureTestSupport.AssertCountsAsync(fixture, 1, token);
    }

    [Theory]
    [InlineData("closure_history")]
    [InlineData("closure_commands")]
    public async Task Insert_failure_rolls_back_every_effect_and_same_key_can_retry(string table)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token);
        var key = Guid.NewGuid();
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var fault = connection.CreateCommand();
        // table comes only from the two fixed InlineData cases.
        fault.CommandText = $"""
            CREATE FUNCTION order_operations.fail_closure_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'controlled closure failure'; END; $$;
            CREATE TRIGGER fail_closure_insert BEFORE INSERT ON order_operations.{table}
            FOR EACH ROW EXECUTE FUNCTION order_operations.fail_closure_insert();
            """;
        await fault.ExecuteNonQueryAsync(token);
        try
        {
            using var response = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, key, token);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await ClosureTestSupport.AssertCountsAsync(fixture, 0, token);
            Assert.Single(await fixture.ReadLiquidationsAsync(token));
        }
        finally
        {
            fault.CommandText = $"DROP TRIGGER fail_closure_insert ON order_operations.{table}; DROP FUNCTION order_operations.fail_closure_insert();";
            await fault.ExecuteNonQueryAsync(token);
        }
        using var retry = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, key, token);
        await ClosureTestSupport.ReadSuccessAsync(retry, token);
        await ClosureTestSupport.AssertCountsAsync(fixture, 1, token);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("orphan")]
    [InlineData("time")]
    public async Task Contradictory_terminal_state_is_ineligible_and_cannot_close(string scenario)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await ClosureTestSupport.CreateOrderAsync(fixture, token, liquidate: scenario != "orphan");
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var orderId = Guid.Parse(order.OperationalReference);
        if (scenario == "pending")
        {
            db.PendingCompositions.Add(new PendingComposition(Guid.CreateVersion7(), orderId, DateTimeOffset.UtcNow, fixture.DefaultOrderOperationsActor.IdentityId));
        }
        else
        {
            db.Closures.Add(new Closure(Guid.CreateVersion7(), orderId, DateTimeOffset.UtcNow.AddDays(-1), fixture.DefaultOrderOperationsActor.IdentityId));
        }
        await db.SaveChangesAsync(token);
        var read = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, order.OperationalReference, token);
        Assert.False(read.IsClosureEligible);
        using var response = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, order.OperationalReference, Guid.NewGuid(), token);
        await ClosureTestSupport.AssertProblemAsync(response, HttpStatusCode.Conflict, "state_inconsistent", token);
        Assert.Empty(await db.ClosureHistory.ToArrayAsync(token));
        Assert.Empty(await db.ClosureCommands.ToArrayAsync(token));
    }
}
