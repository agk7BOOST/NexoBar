using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.OrderOperations;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class SubsequentConfirmationApiTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Second_confirmation_preserves_previous_state_history_and_context()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var firstProduct = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var secondProduct = await fixture.CreateProductAsync("Soda", "20", cancellationToken);
        var first = await CreateOrderAsync(firstProduct.Id, 1, "Mesa 7", cancellationToken);

        OrderState before;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            before = await ReadOrderStateAsync(
                scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>(),
                Guid.Parse(first.OperationalReference),
                cancellationToken);
        }

        using var response = await PostSubsequentAsync(
            first.OperationalReference,
            Request((firstProduct.Id, 2), (secondProduct.Id, 3)),
            NewIdempotencyKey(),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var confirmed = await ReadSubsequentAsync(response, cancellationToken);
        Assert.Equal(first.OperationalReference, confirmed.OperationalReference);
        AssertUuidVersion(confirmed.Incorporation.Id, 7);
        Assert.Equal(2, confirmed.Incorporation.Ordinal);
        Assert.Equal(TimeSpan.Zero, confirmed.Incorporation.ConfirmedAt.Offset);
        Assert.Equal(2, confirmed.Incorporation.Items.Count);
        Assert.Contains(confirmed.Incorporation.Items, item =>
            item.ProductId == firstProduct.Id && item.Quantity == 2 && item.AppliedPrice == "10");
        Assert.Contains(confirmed.Incorporation.Items, item =>
            item.ProductId == secondProduct.Id && item.Quantity == 3 && item.AppliedPrice == "20");

        await using var verificationScope = fixture.Services.CreateAsyncScope();
        var dbContext = verificationScope.ServiceProvider
            .GetRequiredService<OrderOperationsDbContext>();
        var after = await ReadOrderStateAsync(
            dbContext,
            Guid.Parse(first.OperationalReference),
            cancellationToken);
        Assert.Equal("Mesa 7", after.Context);
        Assert.Equal(before.Incorporations.Single(), after.Incorporations.Single(i => i.Ordinal == 1));
        Assert.Equal(before.Contents.Single(), after.Contents.Single(c =>
            c.IncorporationId == first.FirstIncorporation.Id));
        Assert.Equal(before.History.Single(), after.History.Single(h =>
            h.IncorporationId == first.FirstIncorporation.Id));
        var newHistory = Assert.Single(after.History, h =>
            h.IncorporationId == confirmed.Incorporation.Id);
        AssertUuidVersion(newHistory.Id, 7);
        Assert.Equal("Mesa 7", newHistory.ConfirmedContext);
        Assert.Equal(confirmed.Incorporation.ConfirmedAt, newHistory.OccurredAt);
        Assert.Equal(new SubsequentPersistenceCounts(2, 3, 2, 1, 2),
            await fixture.CountSubsequentEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Real_price_change_is_prospective_and_query_preserves_both_prices()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var first = await CreateOrderAsync(product.Id, 1, "Mesa 7", cancellationToken);

        using var priceChange = await PostPriceChangeAsync(
            product.Id,
            "10",
            "12",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, priceChange.StatusCode);

        using var secondResponse = await PostSubsequentAsync(
            first.OperationalReference,
            Request((product.Id, 2)),
            NewIdempotencyKey(),
            cancellationToken);
        var second = await ReadSubsequentAsync(secondResponse, cancellationToken);
        Assert.Equal("12", Assert.Single(second.Incorporation.Items).AppliedPrice);

        using var queryResponse = await fixture.Client.GetAsync(
            $"/api/order-operations/orders/{first.OperationalReference}",
            cancellationToken);
        queryResponse.EnsureSuccessStatusCode();
        var order = Assert.IsType<OrderQueryResponse>(
            await queryResponse.Content.ReadFromJsonAsync<OrderQueryResponse>(cancellationToken));
        Assert.Collection(
            order.Incorporations,
            incorporation =>
            {
                Assert.Equal(1, incorporation.Ordinal);
                Assert.Equal("10", Assert.Single(incorporation.Items).AppliedPrice);
            },
            incorporation =>
            {
                Assert.Equal(2, incorporation.Ordinal);
                Assert.Equal("12", Assert.Single(incorporation.Items).AppliedPrice);
            });
    }

    [Fact]
    public async Task Same_key_and_same_intent_replays_identical_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var first = await CreateOrderAsync(product.Id, 1, "Mesa 7", cancellationToken);
        var key = NewIdempotencyKey();
        var request = Request((product.Id, 2));

        using var confirmation = await PostSubsequentAsync(
            first.OperationalReference, request, key, cancellationToken);
        using var replay = await PostSubsequentAsync(
            first.OperationalReference, request, key, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(
            await confirmation.Content.ReadAsStringAsync(cancellationToken),
            await replay.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(new SubsequentPersistenceCounts(2, 2, 2, 1, 1),
            await fixture.CountSubsequentEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Item_order_does_not_change_idempotent_intent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var water = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var soda = await fixture.CreateProductAsync("Soda", "20", cancellationToken);
        var first = await CreateOrderAsync(water.Id, 1, "Mesa 7", cancellationToken);
        var key = NewIdempotencyKey();

        using var confirmation = await PostSubsequentAsync(
            first.OperationalReference,
            Request((water.Id, 2), (soda.Id, 3)),
            key,
            cancellationToken);
        using var replay = await PostSubsequentAsync(
            first.OperationalReference,
            Request((soda.Id, 3), (water.Id, 2)),
            key,
            cancellationToken);

        Assert.Equal(
            await confirmation.Content.ReadAsStringAsync(cancellationToken),
            await replay.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(new SubsequentPersistenceCounts(2, 3, 2, 1, 2),
            await fixture.CountSubsequentEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Same_key_with_different_order_or_items_conflicts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var firstOrder = await CreateOrderAsync(product.Id, 1, "Mesa 1", cancellationToken);
        var secondOrder = await CreateOrderAsync(product.Id, 1, "Mesa 2", cancellationToken);
        var key = NewIdempotencyKey();

        using var confirmation = await PostSubsequentAsync(
            firstOrder.OperationalReference,
            Request((product.Id, 2)),
            key,
            cancellationToken);
        using var differentOrder = await PostSubsequentAsync(
            secondOrder.OperationalReference,
            Request((product.Id, 2)),
            key,
            cancellationToken);
        using var differentItems = await PostSubsequentAsync(
            firstOrder.OperationalReference,
            Request((product.Id, 3)),
            key,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
        await AssertProblemAsync(
            differentOrder,
            HttpStatusCode.Conflict,
            "order_operations.subsequent_confirmation.idempotency_key_conflict",
            cancellationToken);
        await AssertProblemAsync(
            differentItems,
            HttpStatusCode.Conflict,
            "order_operations.subsequent_confirmation.idempotency_key_conflict",
            cancellationToken);
        Assert.Equal(3, (await fixture.CountSubsequentEffectsAsync(cancellationToken)).Incorporations);
    }

    [Fact]
    public async Task Concurrent_same_key_creates_one_incorporation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var first = await CreateOrderAsync(product.Id, 1, "Mesa 7", cancellationToken);
        var catalogLocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCatalog = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithCatalogDecorator(
            services => new BlockingCatalogDecorator(
                new OrderConfirmationCatalog(
                    services.GetRequiredService<CatalogDbContext>()),
                catalogLocked,
                releaseCatalog));
        using var client = application.CreateClient();
        var key = NewIdempotencyKey();
        var request = Request((product.Id, 2));

        var firstTask = PostSubsequentAsync(
            first.OperationalReference, request, key, cancellationToken, client);
        try
        {
            await catalogLocked.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var secondTask = PostSubsequentAsync(
                first.OperationalReference, request, key, cancellationToken, client);
            Assert.True(await WaitForAdvisoryLockWaiterAsync(cancellationToken));
            releaseCatalog.TrySetResult();

            var responses = await Task.WhenAll(firstTask, secondTask);
            try
            {
                Assert.All(responses, response =>
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode));
                Assert.Equal(
                    await responses[0].Content.ReadAsStringAsync(cancellationToken),
                    await responses[1].Content.ReadAsStringAsync(cancellationToken));
                Assert.Equal(new SubsequentPersistenceCounts(2, 2, 2, 1, 1),
                    await fixture.CountSubsequentEffectsAsync(cancellationToken));
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
        finally
        {
            releaseCatalog.TrySetResult();
        }
    }

    [Fact]
    public async Task Replay_survives_host_restart_and_ignores_later_catalog_price()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var first = await CreateOrderAsync(product.Id, 1, "Mesa 7", cancellationToken);
        var responsibilityId = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(
            product.Id,
            responsibilityId,
            cancellationToken);
        var key = NewIdempotencyKey();
        var request = Request((product.Id, 2));
        using var confirmation = await PostSubsequentAsync(
            first.OperationalReference, request, key, cancellationToken);
        var originalBody = await confirmation.Content.ReadAsStringAsync(cancellationToken);
        using var priceChange = await PostPriceChangeAsync(
            product.Id, "10", "12", cancellationToken);
        priceChange.EnsureSuccessStatusCode();

        await fixture.RestartApplicationAsync(cancellationToken);
        var unexpectedCatalog = new UnexpectedCatalogCapability();
        await using var application = fixture.CreateApplicationWithCatalog(unexpectedCatalog);
        using var client = application.CreateClient();
        using var replay = await PostSubsequentAsync(
            first.OperationalReference, request, key, cancellationToken, client);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync(cancellationToken));
        Assert.False(unexpectedCatalog.WasCalled);
        Assert.Equal(1, await fixture.CountPreparationWorkAsync(cancellationToken));
        Assert.Equal("10", Assert.Single(
            (await ReadSubsequentAsync(replay, cancellationToken)).Incorporation.Items).AppliedPrice);
    }

    [Fact]
    public async Task Different_keys_on_same_order_wait_for_row_lock_and_create_ordinals_two_and_three()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var first = await CreateOrderAsync(product.Id, 1, "Mesa 7", cancellationToken);
        var orderId = Guid.Parse(first.OperationalReference);

        await using var blockerConnection = new NpgsqlConnection(fixture.ConnectionString);
        await blockerConnection.OpenAsync(cancellationToken);
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync(
            cancellationToken);
        await LockOrderAsync(blockerConnection, blockerTransaction, orderId, cancellationToken);

        var firstTask = PostSubsequentAsync(
            first.OperationalReference,
            Request((product.Id, 2)),
            NewIdempotencyKey(),
            cancellationToken);
        var secondTask = PostSubsequentAsync(
            first.OperationalReference,
            Request((product.Id, 3)),
            NewIdempotencyKey(),
            cancellationToken);

        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(
            2,
            TimeSpan.FromSeconds(10),
            cancellationToken));
        await blockerTransaction.RollbackAsync(cancellationToken);

        var responses = await Task.WhenAll(firstTask, secondTask);
        try
        {
            Assert.All(responses, response =>
                Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            var results = await Task.WhenAll(responses.Select(response =>
                ReadSubsequentAsync(response, cancellationToken)));
            Assert.Equal(new[] { 2, 3 },
                results.Select(result => result.Incorporation.Ordinal).Order().ToArray());

            await using var scope = fixture.Services.CreateAsyncScope();
            var ordinals = await scope.ServiceProvider
                .GetRequiredService<OrderOperationsDbContext>()
                .Incorporations.AsNoTracking()
                .Where(incorporation => incorporation.OrderId == orderId)
                .OrderBy(incorporation => incorporation.Ordinal)
                .Select(incorporation => incorporation.Ordinal)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(new[] { 1, 2, 3 }, ordinals);
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
    public async Task Lock_on_one_order_does_not_block_confirmation_on_another_order()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var blockedOrder = await CreateOrderAsync(product.Id, 1, "Mesa 1", cancellationToken);
        var freeOrder = await CreateOrderAsync(product.Id, 1, "Mesa 2", cancellationToken);

        await using var blockerConnection = new NpgsqlConnection(fixture.ConnectionString);
        await blockerConnection.OpenAsync(cancellationToken);
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync(
            cancellationToken);
        await LockOrderAsync(
            blockerConnection,
            blockerTransaction,
            Guid.Parse(blockedOrder.OperationalReference),
            cancellationToken);

        var blockedTask = PostSubsequentAsync(
            blockedOrder.OperationalReference,
            Request((product.Id, 2)),
            NewIdempotencyKey(),
            cancellationToken);
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(
            1,
            TimeSpan.FromSeconds(10),
            cancellationToken));

        using var freeResponse = await PostSubsequentAsync(
                freeOrder.OperationalReference,
                Request((product.Id, 2)),
                NewIdempotencyKey(),
                cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, freeResponse.StatusCode);

        await blockerTransaction.RollbackAsync(cancellationToken);
        using var blockedResponse = await blockedTask.WaitAsync(
            TimeSpan.FromSeconds(10), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, blockedResponse.StatusCode);
    }

    [Fact]
    public async Task Missing_inactive_and_unavailable_products_roll_back()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        foreach (var condition in new[] { "missing", "inactive", "unavailable" })
        {
            await fixture.ResetAsync(cancellationToken);
            var orderProduct = await fixture.CreateProductAsync("Base", "10", cancellationToken);
            var first = await CreateOrderAsync(orderProduct.Id, 1, "Mesa 7", cancellationToken);
            var candidateId = Guid.NewGuid();
            var expectedCode = "order_operations.first_confirmation.product_not_current";

            if (condition != "missing")
            {
                var candidate = await fixture.CreateProductAsync("Candidate", "20", cancellationToken);
                candidateId = candidate.Id;
                await fixture.SetProductStateAsync(
                    candidate.Id,
                    condition != "inactive",
                    condition != "unavailable",
                    cancellationToken);
                if (condition == "unavailable")
                {
                    expectedCode = "order_operations.first_confirmation.product_unavailable";
                }
            }

            using var response = await PostSubsequentAsync(
                first.OperationalReference,
                Request((candidateId, 1)),
                NewIdempotencyKey(),
                cancellationToken);
            await AssertProblemAsync(
                response,
                HttpStatusCode.Conflict,
                expectedCode,
                cancellationToken);
            Assert.Equal(new SubsequentPersistenceCounts(1, 1, 1, 0, 0),
                await fixture.CountSubsequentEffectsAsync(cancellationToken));
        }
    }

    [Fact]
    public async Task Product_requiring_preparation_creates_subsequent_work()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var first = await CreateOrderAsync(product.Id, 1, "Mesa 7", cancellationToken);
        var responsibilityId = Guid.CreateVersion7();
        var replacement = new FixedCatalogCapability(
            true,
            true,
            true,
            10m,
            responsibilityId);
        await using var application = fixture.CreateApplicationWithCatalog(replacement);
        using var client = application.CreateClient();

        using var response = await PostSubsequentAsync(
            first.OperationalReference,
            Request((product.Id, 1)),
            NewIdempotencyKey(),
            cancellationToken,
            client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var confirmed = await ReadSubsequentAsync(response, cancellationToken);
        Assert.True(replacement.ObservedActiveTransaction);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(cancellationToken));
        Assert.Equal(confirmed.Incorporation.Id, work.IncorporationId);
        Assert.Equal(product.Id, work.ProductId);
        Assert.Equal(responsibilityId, work.PreparationResponsibilityId);
        Assert.Equal(1, work.TotalQuantity);
        Assert.Equal(1, work.PendingQuantity);
        Assert.Equal(0, work.InPreparationQuantity);
        Assert.Equal(0, work.ReadyQuantity);
    }

    [Fact]
    public async Task Invalid_items_are_rejected_without_subsequent_effects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var first = await CreateOrderAsync(product.Id, 1, "Mesa 7", cancellationToken);

        var cases = new[]
        {
            (Request(), "order_operations.first_confirmation.composition_empty"),
            (Request((product.Id, 0)), "order_operations.first_confirmation.quantity_invalid"),
            (Request((product.Id, -1)), "order_operations.first_confirmation.quantity_invalid"),
            (Request((product.Id, 1), (product.Id, 2)),
                "order_operations.first_confirmation.duplicate_product")
        };

        foreach (var (request, code) in cases)
        {
            using var response = await PostSubsequentAsync(
                first.OperationalReference,
                request,
                NewIdempotencyKey(),
                cancellationToken);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, code, cancellationToken);
        }

        Assert.Equal(new SubsequentPersistenceCounts(1, 1, 1, 0, 0),
            await fixture.CountSubsequentEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Empty_product_id_is_rejected_without_catalog_or_effects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var first = await CreateOrderAsync(product.Id, 1, "Mesa 7", cancellationToken);
        var unexpectedCatalog = new UnexpectedCatalogCapability();
        await using var application = fixture.CreateApplicationWithCatalog(unexpectedCatalog);
        using var client = application.CreateClient();

        using var response = await PostSubsequentAsync(
            first.OperationalReference,
            Request((Guid.Empty, 1)),
            NewIdempotencyKey(),
            cancellationToken,
            client);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(unexpectedCatalog.WasCalled);
        Assert.Equal(new SubsequentPersistenceCounts(1, 1, 1, 0, 0),
            await fixture.CountSubsequentEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Missing_and_non_version_four_idempotency_keys_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var first = await CreateOrderAsync(product.Id, 1, "Mesa 7", cancellationToken);

        using var missing = await PostSubsequentAsync(
            first.OperationalReference,
            Request((product.Id, 1)),
            null,
            cancellationToken);
        await AssertProblemAsync(
            missing,
            HttpStatusCode.BadRequest,
            "order_operations.subsequent_confirmation.idempotency_key_required",
            cancellationToken);

        using var invalid = await PostSubsequentAsync(
            first.OperationalReference,
            Request((product.Id, 1)),
            Guid.CreateVersion7().ToString("D"),
            cancellationToken);
        await AssertProblemAsync(
            invalid,
            HttpStatusCode.BadRequest,
            "order_operations.subsequent_confirmation.idempotency_key_invalid",
            cancellationToken);
        Assert.Equal(new SubsequentPersistenceCounts(1, 1, 1, 0, 0),
            await fixture.CountSubsequentEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Invalid_and_unknown_operational_references_are_distinguished()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);

        using var invalid = await PostSubsequentAsync(
            "not-a-valid-reference",
            Request((Guid.NewGuid(), 1)),
            NewIdempotencyKey(),
            cancellationToken);
        using var invalidProblem = await AssertProblemAsync(
            invalid,
            HttpStatusCode.BadRequest,
            "order_operations.order.operational_reference_invalid",
            cancellationToken);
        Assert.DoesNotContain(
            "uuid",
            invalidProblem.RootElement.GetProperty("detail").GetString(),
            StringComparison.OrdinalIgnoreCase);

        using var missing = await PostSubsequentAsync(
            Guid.CreateVersion7().ToString("D"),
            Request((Guid.NewGuid(), 1)),
            NewIdempotencyKey(),
            cancellationToken);
        await AssertProblemAsync(
            missing,
            HttpStatusCode.NotFound,
            "order_operations.order.not_found",
            cancellationToken);
    }

    [Fact]
    public async Task Unknown_authoritative_json_properties_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var first = await CreateOrderAsync(product.Id, 1, "Mesa 7", cancellationToken);

        foreach (var body in new[]
                 {
                     $$"""
                     {"items":[{"productId":"{{product.Id}}","quantity":1,"price":"10"}]}
                     """,
                     $$"""
                     {"items":[{"productId":"{{product.Id}}","quantity":1}],"context":"Mesa 8"}
                     """
                 })
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/order-operations/orders/{first.OperationalReference}/confirmations")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            message.Headers.Add("Idempotency-Key", NewIdempotencyKey());
            using var response = await fixture.Client.SendAsync(message, cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(new SubsequentPersistenceCounts(1, 1, 1, 0, 0),
            await fixture.CountSubsequentEffectsAsync(cancellationToken));
    }

    [Theory]
    [InlineData("history")]
    [InlineData("command")]
    public async Task Persistence_failure_rolls_back_every_subsequent_effect(string failure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var first = await CreateOrderAsync(product.Id, 1, "Mesa 7", cancellationToken);

        if (failure == "history")
        {
            await fixture.SetHistoryFailureAsync(true, cancellationToken);
        }
        else
        {
            await fixture.SetSubsequentCommandFailureAsync(true, cancellationToken);
        }

        try
        {
            using var response = await PostSubsequentAsync(
                first.OperationalReference,
                Request((product.Id, 2)),
                NewIdempotencyKey(),
                cancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(new SubsequentPersistenceCounts(1, 1, 1, 0, 0),
                await fixture.CountSubsequentEffectsAsync(cancellationToken));
        }
        finally
        {
            if (failure == "history")
            {
                await fixture.SetHistoryFailureAsync(false, cancellationToken);
            }
            else
            {
                await fixture.SetSubsequentCommandFailureAsync(false, cancellationToken);
            }
        }
    }

    [Fact]
    public async Task For_share_blocks_real_price_change_until_subsequent_confirmation_commits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var first = await CreateOrderAsync(product.Id, 1, "Mesa 7", cancellationToken);
        var catalogLocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCatalog = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithCatalogDecorator(
            services => new BlockingCatalogDecorator(
                new OrderConfirmationCatalog(
                    services.GetRequiredService<CatalogDbContext>()),
                catalogLocked,
                releaseCatalog));
        using var client = application.CreateClient();

        var confirmationTask = PostSubsequentAsync(
            first.OperationalReference,
            Request((product.Id, 2)),
            NewIdempotencyKey(),
            cancellationToken,
            client);
        try
        {
            await catalogLocked.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var priceChangeTask = PostPriceChangeAsync(
                product.Id, "10", "12", cancellationToken, client);
            Assert.True(await fixture.WaitForPriceUpdateLockAsync(
                TimeSpan.FromSeconds(10), cancellationToken));
            releaseCatalog.TrySetResult();

            using var confirmation = await confirmationTask.WaitAsync(
                TimeSpan.FromSeconds(10), cancellationToken);
            using var priceChange = await priceChangeTask.WaitAsync(
                TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
            Assert.Equal(HttpStatusCode.OK, priceChange.StatusCode);
            Assert.Equal("10", Assert.Single(
                (await ReadSubsequentAsync(confirmation, cancellationToken))
                .Incorporation.Items).AppliedPrice);
        }
        finally
        {
            releaseCatalog.TrySetResult();
        }
    }

    [Fact]
    public async Task OpenApi_describes_the_opaque_subsequent_confirmation_contract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await fixture.Client.GetAsync("/openapi/v1.json", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/order-operations/orders/{operationalReference}/confirmations")
            .GetProperty("post");
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
        var operationalReference = parameters.Single(parameter =>
            parameter.GetProperty("name").GetString() == "operationalReference");
        Assert.True(operationalReference.GetProperty("required").GetBoolean());
        var referenceSchema = operationalReference.GetProperty("schema");
        AssertSchemaType(referenceSchema, "string");
        Assert.False(referenceSchema.TryGetProperty("format", out _));
        Assert.True(parameters.Single(parameter =>
            parameter.GetProperty("name").GetString() == "Idempotency-Key")
            .GetProperty("required").GetBoolean());
        foreach (var status in new[] { "201", "400", "404", "409" })
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal(
            new[] { "items" },
            schemas.GetProperty(nameof(SubsequentConfirmationRequest))
                .GetProperty("properties").EnumerateObject()
                .Select(property => property.Name).ToArray());
        Assert.Equal(
            new[] { "productId", "quantity" },
            schemas.GetProperty(nameof(SubsequentConfirmationItemRequest))
                .GetProperty("properties").EnumerateObject()
                .Select(property => property.Name).ToArray());
        AssertSchemaType(
            schemas.GetProperty(nameof(SubsequentConfirmationResponse))
                .GetProperty("properties").GetProperty("operationalReference"),
            "string");
        AssertSchemaType(
            schemas.GetProperty(nameof(ConfirmedItemResponse))
                .GetProperty("properties").GetProperty("appliedPrice"),
            "string");
    }

    private async Task<FirstConfirmationResponse> CreateOrderAsync(
        Guid productId,
        int quantity,
        string context,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                context,
                [new FirstConfirmationItemRequest(productId, quantity)]))
        };
        message.Headers.Add("Idempotency-Key", NewIdempotencyKey());
        using var response = await fixture.Client.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(cancellationToken));
    }

    private async Task<HttpResponseMessage> PostSubsequentAsync(
        string operationalReference,
        object request,
        string? key,
        CancellationToken cancellationToken,
        HttpClient? client = null)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{operationalReference}/confirmations")
        {
            Content = JsonContent.Create(request, request.GetType())
        };
        if (key is not null)
        {
            message.Headers.Add("Idempotency-Key", key);
        }

        return await (client ?? fixture.Client).SendAsync(message, cancellationToken);
    }

    private async Task<HttpResponseMessage> PostPriceChangeAsync(
        Guid productId,
        string expectedPrice,
        string newPrice,
        CancellationToken cancellationToken,
        HttpClient? client = null)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/catalog/products/{productId}/price-changes")
        {
            Content = JsonContent.Create(new ChangeProductPriceRequest(
                expectedPrice,
                newPrice))
        };
        message.Headers.Add("Idempotency-Key", NewIdempotencyKey());
        return await (client ?? fixture.Client).SendAsync(message, cancellationToken);
    }

    private async Task<bool> WaitForAdvisoryLockWaiterAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND wait_event_type = 'Lock'
                      AND wait_event = 'advisory'
                      AND query LIKE 'SELECT pg_advisory_xact_lock%')
                """;
            if (Assert.IsType<bool>(await command.ExecuteScalarAsync(cancellationToken)))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        return false;
    }

    private static async Task LockOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id FROM order_operations.orders WHERE id = @id FOR UPDATE";
        command.Parameters.AddWithValue("id", orderId);
        Assert.Equal(orderId, Assert.IsType<Guid>(
            await command.ExecuteScalarAsync(cancellationToken)));
    }

    private static SubsequentConfirmationRequest Request(
        params (Guid ProductId, int Quantity)[] items) =>
        new(items.Select(item => new SubsequentConfirmationItemRequest(
            item.ProductId,
            item.Quantity)).ToArray());

    private static async Task<SubsequentConfirmationResponse> ReadSubsequentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<SubsequentConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<SubsequentConfirmationResponse>(
                cancellationToken));
    }

    private static async Task<JsonDocument> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        return problem;
    }

    private static async Task<OrderState> ReadOrderStateAsync(
        OrderOperationsDbContext dbContext,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var context = await dbContext.Orders.AsNoTracking()
            .Where(order => order.Id == orderId)
            .Select(order => order.Context)
            .SingleAsync(cancellationToken);
        var incorporations = await dbContext.Incorporations.AsNoTracking()
            .Where(incorporation => incorporation.OrderId == orderId)
            .OrderBy(incorporation => incorporation.Ordinal)
            .Select(incorporation => new IncorporationSnapshot(
                incorporation.Id,
                incorporation.OrderId,
                incorporation.Ordinal))
            .ToArrayAsync(cancellationToken);
        var ids = incorporations.Select(incorporation => incorporation.Id).ToArray();
        var contents = await dbContext.IncorporationContents.AsNoTracking()
            .Where(content => ids.Contains(content.IncorporationId))
            .OrderBy(content => content.IncorporationId)
            .ThenBy(content => content.ProductId)
            .Select(content => new ContentSnapshot(
                content.IncorporationId,
                content.ProductId,
                content.Quantity,
                content.AppliedPrice))
            .ToArrayAsync(cancellationToken);
        var history = await dbContext.ConfirmationHistory.AsNoTracking()
            .Where(entry => ids.Contains(entry.IncorporationId))
            .OrderBy(entry => entry.IncorporationId)
            .Select(entry => new HistorySnapshot(
                entry.Id,
                entry.IncorporationId,
                entry.ConfirmedContext,
                entry.OccurredAt))
            .ToArrayAsync(cancellationToken);
        return new OrderState(context, incorporations, contents, history);
    }

    private static void AssertUuidVersion(Guid value, int expectedVersion)
    {
        var bytes = value.ToByteArray(bigEndian: true);
        Assert.Equal(expectedVersion, bytes[6] >> 4);
        Assert.Equal(0x80, bytes[8] & 0xc0);
    }

    private static void AssertSchemaType(JsonElement schema, string expectedType)
    {
        var type = schema.GetProperty("type");
        if (type.ValueKind == JsonValueKind.String)
        {
            Assert.Equal(expectedType, type.GetString());
            return;
        }

        Assert.Contains(expectedType, type.EnumerateArray().Select(value => value.GetString()));
    }

    private static string NewIdempotencyKey() => Guid.NewGuid().ToString("D");
}

internal sealed record OrderState(
    string Context,
    IReadOnlyList<IncorporationSnapshot> Incorporations,
    IReadOnlyList<ContentSnapshot> Contents,
    IReadOnlyList<HistorySnapshot> History);

internal sealed record IncorporationSnapshot(Guid Id, Guid OrderId, int Ordinal);

internal sealed record ContentSnapshot(
    Guid IncorporationId,
    Guid ProductId,
    int Quantity,
    decimal AppliedPrice);

internal sealed record HistorySnapshot(
    Guid Id,
    Guid IncorporationId,
    string ConfirmedContext,
    DateTimeOffset OccurredAt);
