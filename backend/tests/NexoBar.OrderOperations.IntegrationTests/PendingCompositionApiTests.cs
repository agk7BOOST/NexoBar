using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.OrderOperations;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PendingCompositionApiTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Start_read_and_discard_are_authoritative_and_discard_is_not_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateOrderAsync(token);

        using var emptyResponse = await fixture.OrderOperationsClient.GetAsync(
            PendingPath(order.OperationalReference), token);
        emptyResponse.EnsureSuccessStatusCode();
        Assert.Null((await ReadCurrentAsync(emptyResponse, token)).PendingComposition);

        var started = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, token);
        Assert.Equal(fixture.DefaultOrderOperationsActor.IdentityId,
            started.CreatedByIdentityId);
        Assert.Equal(7, started.PendingCompositionId.Version);
        Assert.Equal(TimeSpan.Zero, started.CreatedAt.Offset);

        using var readResponse = await fixture.OrderOperationsClient.GetAsync(
            PendingPath(order.OperationalReference), token);
        var current = Assert.IsType<PendingCompositionResponse>(
            (await ReadCurrentAsync(readResponse, token)).PendingComposition);
        Assert.Equal(started, current);

        using var discard = await DiscardAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            started.PendingCompositionId,
            Guid.NewGuid(),
            token);
        Assert.Equal(HttpStatusCode.NoContent, discard.StatusCode);

        using var afterResponse = await fixture.OrderOperationsClient.GetAsync(
            PendingPath(order.OperationalReference), token);
        Assert.Null((await ReadCurrentAsync(afterResponse, token)).PendingComposition);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Equal(0, await db.PendingCompositions.CountAsync(token));
        Assert.Equal(2, await db.PendingCompositionCommands.CountAsync(token));
        Assert.Equal(1, await db.ConfirmationHistory.CountAsync(token));
    }

    [Fact]
    public async Task Marker_survives_restart_and_database_rejects_a_second_for_the_order()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateOrderAsync(token);
        var started = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, token);

        await fixture.RestartApplicationAsync(token);
        using var response = await fixture.OrderOperationsClient.GetAsync(
            PendingPath(order.OperationalReference), token);
        Assert.Equal(started,
            (await ReadCurrentAsync(response, token)).PendingComposition);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        db.PendingCompositions.Add(new PendingComposition(
            Guid.CreateVersion7(),
            Guid.Parse(order.OperationalReference),
            DateTimeOffset.UtcNow,
            fixture.DefaultOrderOperationsActor.IdentityId));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
    }

    [Fact]
    public async Task New_pending_commands_require_session_and_current_responsibility()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateOrderAsync(token);
        var marker = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, token);
        var unauthorizedActor = await fixture.CreateDeliveryActorAsync(
            hasOrderOperations: false,
            hasPreparation: false,
            enabledResponsibilityId: null,
            token);
        using var forbidden = await fixture.LoginAsync(unauthorizedActor, token);

        using var anonymousRead = await fixture.Client.GetAsync(
            PendingPath(order.OperationalReference), token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
        using var forbiddenRead = await forbidden.GetAsync(
            PendingPath(order.OperationalReference), token);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRead.StatusCode);

        using var anonymousStart = await StartAsync(
            fixture.Client, order.OperationalReference, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousStart.StatusCode);
        using var forbiddenStart = await StartAsync(
            forbidden, order.OperationalReference, Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenStart.StatusCode);

        using var anonymousDiscard = await DiscardAsync(
            fixture.Client,
            order.OperationalReference,
            marker.PendingCompositionId,
            Guid.NewGuid(),
            token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousDiscard.StatusCode);
        using var forbiddenDiscard = await DiscardAsync(
            forbidden,
            order.OperationalReference,
            marker.PendingCompositionId,
            Guid.NewGuid(),
            token);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenDiscard.StatusCode);
    }

    [Fact]
    public async Task Inactive_identity_is_unauthenticated_and_mutations_require_antiforgery()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateOrderAsync(token);

        using var missingAntiforgery = new HttpRequestMessage(
            HttpMethod.Post,
            PendingPath(order.OperationalReference));
        missingAntiforgery.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var rejected = await fixture.OrderOperationsClient.SendAsync(
            missingAntiforgery, token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        await fixture.SetIdentityActiveAsync(
            fixture.DefaultOrderOperationsActor.IdentityId,
            false,
            token);
        using var read = await fixture.OrderOperationsClient.GetAsync(
            PendingPath(order.OperationalReference), token);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        using var start = await StartAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            token);
        Assert.Equal(HttpStatusCode.Unauthorized, start.StatusCode);
    }

    [Fact]
    public async Task Start_replay_survives_revocation_but_another_actor_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateOrderAsync(token);
        var key = Guid.NewGuid();

        using var first = await StartAsync(
            fixture.OrderOperationsClient, order.OperationalReference, key, token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = await first.Content.ReadAsStringAsync(token);

        await fixture.RevokeOrderOperationsAssignmentAsync(
            fixture.DefaultOrderOperationsActor.IdentityId, token);
        using var replay = await StartAsync(
            fixture.OrderOperationsClient, order.OperationalReference, key, token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync(token));
        var marker = Assert.IsType<PendingCompositionResponse>(
            await replay.Content.ReadFromJsonAsync<PendingCompositionResponse>(token));
        using var crossKind = await DiscardAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            marker.PendingCompositionId,
            key,
            token);
        await AssertProblemAsync(
            crossKind,
            HttpStatusCode.Conflict,
            "order.pending_composition_idempotency_key_conflict",
            token);

        var otherActor = await fixture.CreateDeliveryActorAsync(
            true, false, null, token);
        using var otherClient = await fixture.LoginAsync(otherActor, token);
        using var actorMismatch = await StartAsync(
            otherClient, order.OperationalReference, key, token);
        await AssertProblemAsync(
            actorMismatch,
            HttpStatusCode.Conflict,
            "order.pending_composition_idempotency_key_conflict",
            token);
    }

    [Fact]
    public async Task Discard_replay_survives_revocation_and_stale_target_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateOrderAsync(token);
        var marker = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, token);
        var discardKey = Guid.NewGuid();

        using var discarded = await DiscardAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            marker.PendingCompositionId,
            discardKey,
            token);
        Assert.Equal(HttpStatusCode.NoContent, discarded.StatusCode);
        await fixture.RevokeOrderOperationsAssignmentAsync(
            fixture.DefaultOrderOperationsActor.IdentityId, token);
        using var replay = await DiscardAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            marker.PendingCompositionId,
            discardKey,
            token);
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);

        var otherActor = await fixture.CreateDeliveryActorAsync(
            true, false, null, token);
        using var otherClient = await fixture.LoginAsync(otherActor, token);
        using var stale = await DiscardAsync(
            otherClient,
            order.OperationalReference,
            marker.PendingCompositionId,
            Guid.NewGuid(),
            token);
        await AssertProblemAsync(
            stale,
            HttpStatusCode.Conflict,
            "order.pending_composition_stale",
            token);
    }

    [Fact]
    public async Task First_and_subsequent_confirmation_require_authorization_and_persist_actor()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "3", token);
        var firstKey = Guid.NewGuid();

        using var anonymousFirst = await ConfirmFirstAsync(
            fixture.Client, product.Id, firstKey, token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousFirst.StatusCode);
        var unauthorizedActor = await fixture.CreateDeliveryActorAsync(
            false, false, null, token);
        using var forbiddenClient = await fixture.LoginAsync(unauthorizedActor, token);
        using var forbiddenFirst = await ConfirmFirstAsync(
            forbiddenClient, product.Id, firstKey, token);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenFirst.StatusCode);

        using var confirmedFirst = await ConfirmFirstAsync(
            fixture.OrderOperationsClient, product.Id, firstKey, token);
        confirmedFirst.EnsureSuccessStatusCode();
        var first = await ReadFirstAsync(confirmedFirst, token);
        var marker = await fixture.StartPendingCompositionAsync(
            first.OperationalReference, token);

        using var anonymousSubsequent = await ConfirmSubsequentAsync(
            fixture.Client,
            first.OperationalReference,
            marker.PendingCompositionId,
            product.Id,
            Guid.NewGuid(),
            token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousSubsequent.StatusCode);
        using var forbiddenSubsequent = await ConfirmSubsequentAsync(
            forbiddenClient,
            first.OperationalReference,
            marker.PendingCompositionId,
            product.Id,
            Guid.NewGuid(),
            token);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenSubsequent.StatusCode);

        using var confirmedSubsequent = await ConfirmSubsequentAsync(
            fixture.OrderOperationsClient,
            first.OperationalReference,
            marker.PendingCompositionId,
            product.Id,
            Guid.NewGuid(),
            token);
        confirmedSubsequent.EnsureSuccessStatusCode();

        await using var scope = fixture.Services.CreateAsyncScope();
        var actors = await scope.ServiceProvider
            .GetRequiredService<OrderOperationsDbContext>()
            .ConfirmationHistory.AsNoTracking()
            .OrderBy(history => history.OccurredAt)
            .Select(history => history.ActorIdentityId)
            .ToArrayAsync(token);
        Assert.Equal(
            [fixture.DefaultOrderOperationsActor.IdentityId,
             fixture.DefaultOrderOperationsActor.IdentityId],
            actors);
    }

    [Fact]
    public async Task Confirmation_replay_uses_original_actor_and_does_not_reauthorize()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "3", token);
        var firstKey = Guid.NewGuid();
        using var first = await ConfirmFirstAsync(
            fixture.OrderOperationsClient, product.Id, firstKey, token);
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadAsStringAsync(token);

        await fixture.RevokeOrderOperationsAssignmentAsync(
            fixture.DefaultOrderOperationsActor.IdentityId, token);
        using var replay = await ConfirmFirstAsync(
            fixture.OrderOperationsClient, product.Id, firstKey, token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync(token));

        var otherActor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var otherClient = await fixture.LoginAsync(otherActor, token);
        using var mismatch = await ConfirmFirstAsync(
            otherClient, product.Id, firstKey, token);
        await AssertProblemAsync(
            mismatch,
            HttpStatusCode.Conflict,
            "order_operations.first_confirmation.idempotency_key_conflict",
            token);
    }

    [Fact]
    public async Task Subsequent_replay_uses_original_actor_and_does_not_reauthorize()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "3", token);
        var order = await CreateOrderAsync(token, product.Id);
        var marker = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, token);
        var key = Guid.NewGuid();
        using var first = await ConfirmSubsequentAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            marker.PendingCompositionId,
            product.Id,
            key,
            token);
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadAsStringAsync(token);

        await fixture.RevokeOrderOperationsAssignmentAsync(
            fixture.DefaultOrderOperationsActor.IdentityId, token);
        using var replay = await ConfirmSubsequentAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            marker.PendingCompositionId,
            product.Id,
            key,
            token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync(token));

        var otherActor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        using var otherClient = await fixture.LoginAsync(otherActor, token);
        using var mismatch = await ConfirmSubsequentAsync(
            otherClient,
            order.OperationalReference,
            marker.PendingCompositionId,
            product.Id,
            key,
            token);
        await AssertProblemAsync(
            mismatch,
            HttpStatusCode.Conflict,
            "order_operations.subsequent_confirmation.idempotency_key_conflict",
            token);
    }

    [Fact]
    public async Task Subsequent_requires_exact_marker_failed_validation_keeps_it_and_success_consumes_it()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "3", token);
        var order = await CreateOrderAsync(token, product.Id);

        using var missing = await ConfirmSubsequentAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            product.Id,
            Guid.NewGuid(),
            token);
        await AssertProblemAsync(
            missing,
            HttpStatusCode.Conflict,
            "order.pending_composition_stale",
            token);

        var marker = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, token);
        using var wrong = await ConfirmSubsequentAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            product.Id,
            Guid.NewGuid(),
            token);
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);

        using var invalidProduct = await ConfirmSubsequentAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            marker.PendingCompositionId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            token);
        Assert.Equal(HttpStatusCode.Conflict, invalidProduct.StatusCode);
        Assert.Equal(marker, await ReadCurrentMarkerAsync(order.OperationalReference, token));

        using var success = await ConfirmSubsequentAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            marker.PendingCompositionId,
            product.Id,
            Guid.NewGuid(),
            token);
        Assert.Equal(HttpStatusCode.Created, success.StatusCode);
        Assert.Null(await ReadCurrentMarkerAsync(order.OperationalReference, token));
    }

    [Fact]
    public async Task Pending_start_rolls_back_marker_when_command_persistence_fails()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateOrderAsync(token);
        await fixture.SetPendingCompositionCommandFailureAsync(true, token);
        try
        {
            using var failed = await StartAsync(
                fixture.OrderOperationsClient,
                order.OperationalReference,
                Guid.NewGuid(),
                token);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Null(await ReadCurrentMarkerAsync(order.OperationalReference, token));
        }
        finally
        {
            await fixture.SetPendingCompositionCommandFailureAsync(false, token);
        }
    }

    [Fact]
    public async Task Two_starts_on_same_order_serialize_to_one_success_and_one_conflict()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateOrderAsync(token);
        var csrf = await OrderOperationsApiFixture.GetAntiforgeryTokenAsync(
            fixture.OrderOperationsClient, token);
        await using var blocker = await LockOrderAsync(order.OperationalReference, token);

        var firstTask = StartAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            csrf,
            token);
        var secondTask = StartAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            csrf,
            token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(
            2, TimeSpan.FromSeconds(10), token));
        await blocker.Transaction.RollbackAsync(token);

        var responses = await Task.WhenAll(firstTask, secondTask);
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            await using var scope = fixture.Services.CreateAsyncScope();
            Assert.Equal(1, await scope.ServiceProvider
                .GetRequiredService<OrderOperationsDbContext>()
                .PendingCompositions.CountAsync(token));
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
    public async Task Confirm_and_discard_same_marker_serialize_and_exactly_one_consumes()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "3", token);
        var order = await CreateOrderAsync(token, product.Id);
        var marker = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, token);
        var csrf = await OrderOperationsApiFixture.GetAntiforgeryTokenAsync(
            fixture.OrderOperationsClient, token);
        await using var blocker = await LockOrderAsync(order.OperationalReference, token);

        var confirmTask = ConfirmSubsequentAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            marker.PendingCompositionId,
            product.Id,
            Guid.NewGuid(),
            csrf,
            token);
        var discardTask = DiscardAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            marker.PendingCompositionId,
            Guid.NewGuid(),
            csrf,
            token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(
            2, TimeSpan.FromSeconds(10), token));
        await blocker.Transaction.RollbackAsync(token);

        using var confirm = await confirmTask;
        using var discard = await discardTask;
        Assert.Single(new[] { confirm, discard }, response =>
            response.StatusCode is HttpStatusCode.Created or HttpStatusCode.NoContent);
        Assert.Single(new[] { confirm, discard }, response =>
            response.StatusCode == HttpStatusCode.Conflict);
        Assert.Null(await ReadCurrentMarkerAsync(order.OperationalReference, token));
    }

    [Fact]
    public async Task Confirm_and_second_start_have_only_the_two_valid_serial_outcomes()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "3", token);
        var order = await CreateOrderAsync(token, product.Id);
        var marker = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, token);
        var csrf = await OrderOperationsApiFixture.GetAntiforgeryTokenAsync(
            fixture.OrderOperationsClient, token);
        await using var blocker = await LockOrderAsync(order.OperationalReference, token);

        var confirmTask = ConfirmSubsequentAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            marker.PendingCompositionId,
            product.Id,
            Guid.NewGuid(),
            csrf,
            token);
        var startTask = StartAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            csrf,
            token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(
            2, TimeSpan.FromSeconds(10), token));
        await blocker.Transaction.RollbackAsync(token);

        using var confirm = await confirmTask;
        using var start = await startTask;
        Assert.Equal(HttpStatusCode.Created, confirm.StatusCode);
        Assert.Contains(start.StatusCode, new[]
        {
            HttpStatusCode.Created,
            HttpStatusCode.Conflict
        });
        Assert.Equal(start.StatusCode == HttpStatusCode.Created,
            await ReadCurrentMarkerAsync(order.OperationalReference, token) is not null);
    }

    [Fact]
    public async Task Order_lock_on_one_order_does_not_block_start_on_another()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var first = await CreateOrderAsync(token);
        var second = await CreateOrderAsync(token);
        var csrf = await OrderOperationsApiFixture.GetAntiforgeryTokenAsync(
            fixture.OrderOperationsClient, token);
        await using var blocker = await LockOrderAsync(first.OperationalReference, token);

        var blockedTask = StartAsync(
            fixture.OrderOperationsClient,
            first.OperationalReference,
            Guid.NewGuid(),
            csrf,
            token);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(
            1, TimeSpan.FromSeconds(10), token));
        using var free = await StartAsync(
            fixture.OrderOperationsClient,
            second.OperationalReference,
            Guid.NewGuid(),
            csrf,
            token).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(HttpStatusCode.Created, free.StatusCode);

        await blocker.Transaction.RollbackAsync(token);
        using var blocked = await blockedTask.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(HttpStatusCode.Created, blocked.StatusCode);
    }

    private async Task<FirstConfirmationResponse> CreateOrderAsync(
        CancellationToken token,
        Guid? productId = null)
    {
        var resolvedProductId = productId ??
            (await fixture.CreateProductAsync(
                $"Producto {Guid.NewGuid():N}", "3", token)).Id;
        using var response = await ConfirmFirstAsync(
            fixture.OrderOperationsClient,
            resolvedProductId,
            Guid.NewGuid(),
            token);
        response.EnsureSuccessStatusCode();
        return await ReadFirstAsync(response, token);
    }

    private static async Task<HttpResponseMessage> StartAsync(
        HttpClient client,
        string orderId,
        Guid key,
        CancellationToken token) =>
        await StartAsync(
            client,
            orderId,
            key,
            await OrderOperationsApiFixture.GetAntiforgeryTokenAsync(client, token),
            token);

    private static async Task<HttpResponseMessage> StartAsync(
        HttpClient client,
        string orderId,
        Guid key,
        string antiforgeryToken,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, PendingPath(orderId));
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        request.Headers.Add("X-NexoBar-CSRF", antiforgeryToken);
        return await client.SendAsync(request, token);
    }

    private static async Task<HttpResponseMessage> DiscardAsync(
        HttpClient client,
        string orderId,
        Guid pendingCompositionId,
        Guid key,
        CancellationToken token) =>
        await DiscardAsync(
            client,
            orderId,
            pendingCompositionId,
            key,
            await OrderOperationsApiFixture.GetAntiforgeryTokenAsync(client, token),
            token);

    private static async Task<HttpResponseMessage> DiscardAsync(
        HttpClient client,
        string orderId,
        Guid pendingCompositionId,
        Guid key,
        string antiforgeryToken,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{PendingPath(orderId)}/{pendingCompositionId:D}/discard");
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        request.Headers.Add("X-NexoBar-CSRF", antiforgeryToken);
        return await client.SendAsync(request, token);
    }

    private static async Task<HttpResponseMessage> ConfirmFirstAsync(
        HttpClient client,
        Guid productId,
        Guid key,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                $"Mesa {key:N}",
                [new FirstConfirmationItemRequest(productId, 1)]))
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            client, request, token);
    }

    private static async Task<HttpResponseMessage> ConfirmSubsequentAsync(
        HttpClient client,
        string orderId,
        Guid pendingCompositionId,
        Guid productId,
        Guid key,
        CancellationToken token) =>
        await ConfirmSubsequentAsync(
            client,
            orderId,
            pendingCompositionId,
            productId,
            key,
            await OrderOperationsApiFixture.GetAntiforgeryTokenAsync(client, token),
            token);

    private static async Task<HttpResponseMessage> ConfirmSubsequentAsync(
        HttpClient client,
        string orderId,
        Guid pendingCompositionId,
        Guid productId,
        Guid key,
        string antiforgeryToken,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{orderId}/confirmations")
        {
            Content = JsonContent.Create(new SubsequentConfirmationRequest(
                pendingCompositionId,
                [new SubsequentConfirmationItemRequest(productId, 1)]))
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        request.Headers.Add("X-NexoBar-CSRF", antiforgeryToken);
        return await client.SendAsync(request, token);
    }

    private async Task<PendingCompositionResponse?> ReadCurrentMarkerAsync(
        string orderId,
        CancellationToken token)
    {
        using var response = await fixture.OrderOperationsClient.GetAsync(
            PendingPath(orderId), token);
        return (await ReadCurrentAsync(response, token)).PendingComposition;
    }

    private static async Task<CurrentPendingCompositionResponse> ReadCurrentAsync(
        HttpResponseMessage response,
        CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        return Assert.IsType<CurrentPendingCompositionResponse>(
            await response.Content.ReadFromJsonAsync<CurrentPendingCompositionResponse>(token));
    }

    private static async Task<FirstConfirmationResponse> ReadFirstAsync(
        HttpResponseMessage response,
        CancellationToken token) =>
        Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        CancellationToken token)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(token);
        Assert.Equal(code, Assert.IsType<ProblemPayload>(problem).Code);
    }

    private async Task<OrderBlocker> LockOrderAsync(
        string orderId,
        CancellationToken token)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        var transaction = await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id FROM order_operations.orders WHERE id = @id FOR UPDATE";
        command.Parameters.AddWithValue("id", Guid.Parse(orderId));
        Assert.Equal(Guid.Parse(orderId),
            Assert.IsType<Guid>(await command.ExecuteScalarAsync(token)));
        return new OrderBlocker(connection, transaction);
    }

    private static string PendingPath(string orderId) =>
        $"/api/orders/{orderId}/pending-composition";

    private sealed record ProblemPayload(string Code);

    private sealed class OrderBlocker(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) : IAsyncDisposable
    {
        internal NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
