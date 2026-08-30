using System.Data;
using System.Data.Common;
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
public sealed class FirstConfirmationApiTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Missing_idempotency_key_is_rejected_with_stable_code()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);

        using var response = await PostFirstConfirmationAsync(
            Request("Mesa 7", (Guid.NewGuid(), 1)),
            null,
            cancellationToken);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.first_confirmation.idempotency_key_required",
            cancellationToken);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Non_version_4_idempotency_key_is_rejected_with_stable_code()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);

        using var response = await PostFirstConfirmationAsync(
            Request("Mesa 7", (Guid.NewGuid(), 1)),
            Guid.CreateVersion7().ToString("D"),
            cancellationToken);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.first_confirmation.idempotency_key_invalid",
            cancellationToken);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Happy_path_persists_current_state_history_and_durable_result()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync(
            "Agua tÃ³nica",
            "12345678901234567890.12345678",
            cancellationToken);

        using var response = await PostFirstConfirmationAsync(
            Request("Mesa 7", (product.Id, 2)),
            NewIdempotencyKey(),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var confirmed = await ReadConfirmationAsync(response, cancellationToken);
        var orderId = Guid.Parse(confirmed.OperationalReference);
        AssertUuidVersion(orderId, 7);
        Assert.Equal("Mesa 7", confirmed.Context);
        AssertUuidVersion(confirmed.FirstIncorporation.Id, 7);
        Assert.Equal(TimeSpan.Zero, confirmed.FirstIncorporation.ConfirmedAt.Offset);
        var item = Assert.Single(confirmed.FirstIncorporation.Items);
        Assert.Equal(product.Id, item.ProductId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(product.Price, item.AppliedPrice);

        var snapshot = await fixture.ReadSnapshotAsync(cancellationToken);
        Assert.Equal(orderId, snapshot.Order.Id);
        Assert.Equal("Mesa 7", snapshot.Order.Context);
        Assert.Equal(confirmed.FirstIncorporation.Id, snapshot.Incorporation.Id);
        Assert.Equal(1, snapshot.Incorporation.Ordinal);
        Assert.Equal(snapshot.Order.Id, snapshot.Incorporation.OrderId);
        AssertUuidVersion(snapshot.History.Id, 7);
        Assert.Equal("Mesa 7", snapshot.History.ConfirmedContext);
        Assert.Equal(confirmed.FirstIncorporation.ConfirmedAt, snapshot.History.OccurredAt);
        Assert.Equal(product.Price, Assert.Single(snapshot.Contents).AppliedPrice.ToString());
        Assert.Equal(confirmed.FirstIncorporation.Id, snapshot.Command.ResultIncorporationId);
        Assert.Equal(new PersistenceCounts(1, 1, 1, 1, 1, 1),
            await fixture.CountEffectsAsync(cancellationToken));
        Assert.Equal(0, await fixture.CountPreparationWorkAsync(cancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_context_is_rejected_without_effects(string context)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);

        using var response = await PostFirstConfirmationAsync(
            Request(context, (Guid.NewGuid(), 1)),
            NewIdempotencyKey(),
            cancellationToken);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.first_confirmation.context_required",
            cancellationToken);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Empty_composition_is_rejected_without_effects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);

        using var response = await PostFirstConfirmationAsync(
            Request("Mesa 7"),
            NewIdempotencyKey(),
            cancellationToken);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.first_confirmation.composition_empty",
            cancellationToken);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_quantity_is_rejected_without_effects(int quantity)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var productId = Guid.NewGuid();

        using var response = await PostFirstConfirmationAsync(
            Request("Mesa 7", (productId, quantity)),
            NewIdempotencyKey(),
            cancellationToken);

        await AssertProblemWithProductAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.first_confirmation.quantity_invalid",
            productId,
            cancellationToken);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Duplicate_canonical_line_is_rejected_without_effects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var productId = Guid.NewGuid();

        using var response = await PostFirstConfirmationAsync(
            Request("Mesa 7", (productId, 1), (productId, 2)),
            NewIdempotencyKey(),
            cancellationToken);

        await AssertProblemWithProductAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.confirmation.duplicate_line",
            productId,
            cancellationToken);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Missing_product_is_rejected_without_effects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var productId = Guid.NewGuid();

        using var response = await PostFirstConfirmationAsync(
            Request("Mesa 7", (productId, 1)),
            NewIdempotencyKey(),
            cancellationToken);

        await AssertProblemWithProductAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.first_confirmation.product_not_current",
            productId,
            cancellationToken);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Inactive_product_is_rejected_without_effects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        await fixture.SetProductStateAsync(product.Id, false, true, cancellationToken);

        using var response = await PostFirstConfirmationAsync(
            Request("Mesa 7", (product.Id, 1)),
            NewIdempotencyKey(),
            cancellationToken);

        await AssertProblemWithProductAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.first_confirmation.product_not_current",
            product.Id,
            cancellationToken);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Unavailable_product_is_rejected_without_effects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        await fixture.SetProductStateAsync(product.Id, true, false, cancellationToken);

        using var response = await PostFirstConfirmationAsync(
            Request("Mesa 7", (product.Id, 1)),
            NewIdempotencyKey(),
            cancellationToken);

        await AssertProblemWithProductAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.first_confirmation.product_unavailable",
            product.Id,
            cancellationToken);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Product_requiring_preparation_creates_work_in_active_transaction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var productId = Guid.NewGuid();
        var responsibilityId = Guid.CreateVersion7();
        var replacement = new FixedCatalogCapability(
            isActive: true,
            isAvailable: true,
            requiresPreparation: true,
            price: 10m,
            preparationResponsibilityId: responsibilityId);
        await using var application = fixture.CreateApplicationWithCatalog(replacement);
        using var client = application.CreateClient();

        using var response = await PostFirstConfirmationAsync(
            Request("Mesa 7", (productId, 1)),
            NewIdempotencyKey(),
            cancellationToken,
            client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(replacement.ObservedActiveTransaction);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(cancellationToken));
        Assert.Equal(productId, work.ProductId);
        Assert.Equal(responsibilityId, work.PreparationResponsibilityId);
    }

    [Fact]
    public async Task Same_key_and_same_intent_replays_identical_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10.50", cancellationToken);
        var key = NewIdempotencyKey();
        var request = Request("Mesa 7", (product.Id, 2));

        using var first = await PostFirstConfirmationAsync(request, key, cancellationToken);
        using var replay = await PostFirstConfirmationAsync(request, key, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(first.StatusCode, replay.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(cancellationToken),
            await replay.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(new PersistenceCounts(1, 1, 1, 1, 1, 1),
            await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Item_order_does_not_change_idempotent_intent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var firstProduct = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var secondProduct = await fixture.CreateProductAsync("Soda", "20", cancellationToken);
        var key = NewIdempotencyKey();

        using var first = await PostFirstConfirmationAsync(
            Request("Mesa 7", (secondProduct.Id, 2), (firstProduct.Id, 1)),
            key,
            cancellationToken);
        using var replay = await PostFirstConfirmationAsync(
            Request("Mesa 7", (firstProduct.Id, 1), (secondProduct.Id, 2)),
            key,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(cancellationToken),
            await replay.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(new PersistenceCounts(1, 1, 2, 1, 1, 2),
            await fixture.CountEffectsAsync(cancellationToken));

        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var contents = await dbContext.IncorporationContents.AsNoTracking()
            .OrderBy(content => content.ContentOrdinal)
            .ToArrayAsync(cancellationToken);
        var commandLines = await dbContext.FirstConfirmationCommandContents.AsNoTracking()
            .OrderBy(content => content.LineOrdinal)
            .ToArrayAsync(cancellationToken);
        var canonicalProducts = new[] { firstProduct.Id, secondProduct.Id }
            .OrderBy(productId => productId)
            .ToArray();
        Assert.Equal([1, 2], contents.Select(content => content.ContentOrdinal).ToArray());
        Assert.Equal(canonicalProducts, contents.Select(content => content.ProductId).ToArray());
        Assert.Equal([1, 2], commandLines.Select(line => line.LineOrdinal).ToArray());
        Assert.Equal(canonicalProducts, commandLines.Select(line => line.ProductId).ToArray());
    }

    [Fact]
    public async Task Same_key_and_different_intent_conflicts_without_second_effect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var key = NewIdempotencyKey();

        using var first = await PostFirstConfirmationAsync(
            Request("Mesa 7", (product.Id, 1)), key, cancellationToken);
        using var incompatible = await PostFirstConfirmationAsync(
            Request("Mesa 7", (product.Id, 2)), key, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertProblemAsync(
            incompatible,
            HttpStatusCode.Conflict,
            "order_operations.first_confirmation.idempotency_key_conflict",
            cancellationToken);
        Assert.Equal(new PersistenceCounts(1, 1, 1, 1, 1, 1),
            await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Concurrent_same_key_requests_create_one_effect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var key = NewIdempotencyKey();
        var request = Request("Mesa 7", (product.Id, 1));

        var responses = await Task.WhenAll(
            PostFirstConfirmationAsync(request, key, cancellationToken),
            PostFirstConfirmationAsync(request, key, cancellationToken));

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            Assert.Equal(
                await responses[0].Content.ReadAsStringAsync(cancellationToken),
                await responses[1].Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(new PersistenceCounts(1, 1, 1, 1, 1, 1),
                await fixture.CountEffectsAsync(cancellationToken));
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
    public async Task Replay_is_durable_after_host_restart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var key = NewIdempotencyKey();
        var request = Request("Mesa 7", (product.Id, 1));

        using var first = await PostFirstConfirmationAsync(request, key, cancellationToken);
        var firstBody = await first.Content.ReadAsStringAsync(cancellationToken);
        await fixture.RestartApplicationAsync(cancellationToken);
        using var replay = await PostFirstConfirmationAsync(request, key, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(new PersistenceCounts(1, 1, 1, 1, 1, 1),
            await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Persistence_failure_rolls_back_every_effect_and_allows_retry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var key = NewIdempotencyKey();
        var request = Request("Mesa 7", (product.Id, 1));
        await fixture.SetHistoryFailureAsync(true, cancellationToken);

        try
        {
            using var failed = await PostFirstConfirmationAsync(request, key, cancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(PersistenceCounts.Empty,
                await fixture.CountEffectsAsync(cancellationToken));
        }
        finally
        {
            await fixture.SetHistoryFailureAsync(false, cancellationToken);
        }

        using var retry = await PostFirstConfirmationAsync(request, key, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(new PersistenceCounts(1, 1, 1, 1, 1, 1),
            await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Unknown_authoritative_properties_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);

        foreach (var property in new[]
                 {
                     "name", "price", "availability", "isActive", "requiresPreparation"
                 })
        {
            var body = $$"""
                {"context":"Mesa 7","items":[{"productId":"{{product.Id}}","quantity":1,"{{property}}":"not-authoritative"}]}
                """;
            using var response = await PostRawAsync(body, NewIdempotencyKey(), cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Unknown_top_level_property_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var body = $$"""
            {"context":"Mesa 7","items":[{"productId":"{{product.Id}}","quantity":1}],"price":"10"}
            """;

        using var response = await PostRawAsync(
            body,
            NewIdempotencyKey(),
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    [Theory]
    [InlineData("{\"context\":\"Mesa 7\",\"items\":[{\"quantity\":1}]}")]
    [InlineData("{\"context\":\"Mesa 7\",\"items\":[{\"productId\":\"00000000-0000-0000-0000-000000000000\",\"quantity\":1}]}")]
    [InlineData("{\"context\":\"Mesa 7\",\"items\":[null]}")]
    [InlineData("{\"context\":\"Mesa 7\",\"items\":[{\"productId\":\"01990f3e-5e90-7000-8000-000000000001\"}]}")]
    public async Task Structurally_invalid_items_are_rejected_before_catalog(string body)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var replacement = new UnexpectedCatalogCapability();
        await using var application = fixture.CreateApplicationWithCatalog(replacement);
        using var client = application.CreateClient();

        using var response = await PostRawAsync(
            body,
            NewIdempotencyKey(),
            cancellationToken,
            client);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(replacement.WasCalled);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    [Fact]
    public async Task Same_context_with_distinct_keys_creates_distinct_orders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var request = Request("Mesa 7", (product.Id, 1));

        using var first = await PostFirstConfirmationAsync(
            request, NewIdempotencyKey(), cancellationToken);
        using var second = await PostFirstConfirmationAsync(
            request, NewIdempotencyKey(), cancellationToken);
        var firstResult = await ReadConfirmationAsync(first, cancellationToken);
        var secondResult = await ReadConfirmationAsync(second, cancellationToken);

        Assert.NotEqual(firstResult.OperationalReference, secondResult.OperationalReference);
        Assert.Equal(2, (await fixture.CountEffectsAsync(cancellationToken)).Orders);
    }

    [Fact]
    public async Task Trimmed_context_is_persisted_and_defines_equivalent_intent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var key = NewIdempotencyKey();

        using var first = await PostFirstConfirmationAsync(
            Request(" Mesa 7 ", (product.Id, 1)), key, cancellationToken);
        using var replay = await PostFirstConfirmationAsync(
            Request("Mesa 7", (product.Id, 1)), key, cancellationToken);

        var confirmed = await ReadConfirmationAsync(first, cancellationToken);
        Assert.Equal("Mesa 7", confirmed.Context);
        Assert.Equal(
            JsonSerializer.Serialize(confirmed, JsonSerializerOptions.Web),
            await replay.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal("Mesa 7", (await fixture.ReadSnapshotAsync(cancellationToken)).Order.Context);
    }

    [Fact]
    public async Task Catalog_capability_receives_an_active_postgresql_transaction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var productId = Guid.NewGuid();
        var replacement = new FixedCatalogCapability(true, true, false, 10m);
        await using var application = fixture.CreateApplicationWithCatalog(replacement);
        using var client = application.CreateClient();

        using var response = await PostFirstConfirmationAsync(
            Request("Mesa 7", (productId, 1)),
            NewIdempotencyKey(),
            cancellationToken,
            client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(replacement.ObservedActiveTransaction);
    }

    [Fact]
    public async Task Production_catalog_capability_reads_with_the_supplied_transaction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var productId = Guid.CreateVersion7();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO catalog.products
                    (id, operational_name, price, is_active, is_available, requires_preparation)
                VALUES
                    (@id, @name, @price, @isActive, @isAvailable, @requiresPreparation)
                """;
            insert.Parameters.AddWithValue("id", productId);
            insert.Parameters.AddWithValue("name", "Producto no confirmado");
            insert.Parameters.AddWithValue("price", 42.50m);
            insert.Parameters.AddWithValue("isActive", true);
            insert.Parameters.AddWithValue("isAvailable", true);
            insert.Parameters.AddWithValue("requiresPreparation", false);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(cancellationToken));
        }

        await using (var observer = new NpgsqlConnection(fixture.ConnectionString))
        {
            await observer.OpenAsync(cancellationToken);
            await using var visibility = observer.CreateCommand();
            visibility.CommandText = "SELECT EXISTS (SELECT 1 FROM catalog.products WHERE id = @id)";
            visibility.Parameters.AddWithValue("id", productId);
            Assert.False(Assert.IsType<bool>(
                await visibility.ExecuteScalarAsync(cancellationToken)));
        }

        await using var scope = fixture.Services.CreateAsyncScope();
        var capability = scope.ServiceProvider.GetRequiredService<IOrderConfirmationCatalog>();
        var products = await capability.ReadProductsAsync(
            new[] { productId },
            transaction,
            cancellationToken);

        var product = Assert.Single(products);
        Assert.Equal(productId, product.ProductId);
        Assert.Equal(42.50m, product.Price);
        Assert.True(product.IsActive);
        Assert.True(product.IsAvailable);
        Assert.False(product.RequiresPreparation);

        await transaction.RollbackAsync(cancellationToken);
    }

    [Fact]
    public async Task Later_real_price_change_preserves_original_applied_price()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);

        using var confirmation = await PostFirstConfirmationAsync(
            Request("Mesa 7", (product.Id, 1)),
            NewIdempotencyKey(),
            cancellationToken);
        using var priceChange = await PostPriceChangeAsync(
            fixture.Client,
            product.Id,
            "10",
            "12",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
        Assert.Equal(HttpStatusCode.OK, priceChange.StatusCode);
        var confirmed = await ReadConfirmationAsync(confirmation, cancellationToken);
        Assert.Equal("10", Assert.Single(confirmed.FirstIncorporation.Items).AppliedPrice);
        Assert.Equal(10m, Assert.Single(
            (await fixture.ReadSnapshotAsync(cancellationToken)).Contents).AppliedPrice);
    }

    [Fact]
    public async Task For_share_blocks_real_price_update_until_confirmation_commits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua", "10", cancellationToken);
        var lockAcquired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConfirmation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var application = fixture.CreateApplicationWithCatalogDecorator(
            services => new BlockingCatalogDecorator(
                new OrderConfirmationCatalog(
                    services.GetRequiredService<CatalogDbContext>()),
                lockAcquired,
                releaseConfirmation));
        using var client = application.CreateClient();

        var confirmationTask = PostFirstConfirmationAsync(
            Request("Mesa 7", (product.Id, 1)),
            NewIdempotencyKey(),
            cancellationToken,
            client);
        try
        {
            await lockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            var priceChangeTask = PostPriceChangeAsync(
                client,
                product.Id,
                "10",
                "12",
                cancellationToken);

            Assert.True(
                await fixture.WaitForPriceUpdateLockAsync(
                    TimeSpan.FromSeconds(10),
                    cancellationToken),
                "PostgreSQL did not report the Product price UPDATE waiting on a lock.");

            releaseConfirmation.TrySetResult();

            using var confirmation = await confirmationTask.WaitAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);
            using var priceChange = await priceChangeTask.WaitAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);
            Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
            Assert.Equal(HttpStatusCode.OK, priceChange.StatusCode);
            var confirmed = await ReadConfirmationAsync(confirmation, cancellationToken);
            Assert.Equal("10", Assert.Single(confirmed.FirstIncorporation.Items).AppliedPrice);

            using var currentProductResponse = await client.GetAsync(
                $"/api/catalog/products/{product.Id}",
                cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            currentProductResponse.EnsureSuccessStatusCode();
            var currentProduct = await currentProductResponse.Content
                .ReadFromJsonAsync<ProductResponse>(cancellationToken);
            Assert.NotNull(currentProduct);
            Assert.Equal("12", currentProduct.Price);
            Assert.Equal(10m, Assert.Single(
                (await fixture.ReadSnapshotAsync(cancellationToken)).Contents).AppliedPrice);
        }
        finally
        {
            releaseConfirmation.TrySetResult();
        }
    }

    [Fact]
    public async Task Model_has_no_pending_changes()
    {
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    [Fact]
    public async Task OpenApi_describes_the_first_confirmation_contract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await fixture.Client.GetAsync("/openapi/v1.json", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/order-operations/first-confirmations")
            .GetProperty("post");
        var header = operation.GetProperty("parameters").EnumerateArray()
            .Single(parameter =>
                parameter.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.True(header.GetProperty("required").GetBoolean());

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var requestSchema = schemas.GetProperty(nameof(FirstConfirmationRequest));
        var required = requestSchema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()).ToArray();
        Assert.Contains("context", required);
        Assert.Contains("items", required);
        AssertSchemaType(
            schemas.GetProperty(nameof(FirstConfirmationItemRequest))
                .GetProperty("properties").GetProperty("quantity"),
            "integer");
        AssertSchemaType(
            schemas.GetProperty(nameof(ConfirmedItemResponse))
                .GetProperty("properties").GetProperty("appliedPrice"),
            "string");
        var operationalReferenceSchema = schemas.GetProperty(nameof(FirstConfirmationResponse))
            .GetProperty("properties").GetProperty("operationalReference");
        AssertSchemaType(operationalReferenceSchema, "string");
        Assert.False(operationalReferenceSchema.TryGetProperty("format", out _));
        var requestItemProperties = schemas.GetProperty(nameof(FirstConfirmationItemRequest))
            .GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(new[] { "productId", "quantity", "instruction" },
            requestItemProperties);
        var requiredItemProperties = schemas.GetProperty(nameof(FirstConfirmationItemRequest))
            .GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()).ToArray();
        Assert.Contains("productId", requiredItemProperties);
        Assert.Contains("quantity", requiredItemProperties);
        Assert.DoesNotContain("instruction", requiredItemProperties);
        AssertSchemaType(
            schemas.GetProperty(nameof(FirstConfirmationItemRequest))
                .GetProperty("properties").GetProperty("instruction"),
            "string");
        AssertSchemaType(
            schemas.GetProperty(nameof(ConfirmedItemResponse))
                .GetProperty("properties").GetProperty("instruction"),
            "string");
    }

    private async Task<HttpResponseMessage> PostFirstConfirmationAsync(
        object request,
        string? idempotencyKey,
        CancellationToken cancellationToken,
        HttpClient? client = null)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(request, request.GetType())
        };
        if (idempotencyKey is not null)
        {
            message.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await (client ?? fixture.Client).SendAsync(message, cancellationToken);
    }

    private static async Task<HttpResponseMessage> PostPriceChangeAsync(
        HttpClient client,
        Guid productId,
        string expectedCurrentPrice,
        string newPrice,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/catalog/products/{productId}/price-changes")
        {
            Content = JsonContent.Create(new ChangeProductPriceRequest(
                expectedCurrentPrice,
                newPrice))
        };
        message.Headers.Add("Idempotency-Key", NewIdempotencyKey());
        return await client.SendAsync(message, cancellationToken);
    }

    private Task<HttpResponseMessage> PostRawAsync(
        string body,
        string idempotencyKey,
        CancellationToken cancellationToken,
        HttpClient? client = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return (client ?? fixture.Client).SendAsync(request, cancellationToken);
    }

    private static FirstConfirmationRequest Request(
        string context,
        params (Guid ProductId, int Quantity)[] items) =>
        new(
            context,
            items.Select(item => new FirstConfirmationItemRequest(
                item.ProductId,
                item.Quantity)).ToArray());

    private static async Task<FirstConfirmationResponse> ReadConfirmationAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(cancellationToken));

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task AssertProblemWithProductAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        Guid productId,
        CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(productId, problem.RootElement.GetProperty("productId").GetGuid());
    }

    private static void AssertUuidVersion(Guid value, int expectedVersion) =>
        Assert.Equal(expectedVersion, value.ToByteArray(bigEndian: true)[6] >> 4);

    private static void AssertSchemaType(JsonElement schema, string expectedType)
    {
        var type = schema.GetProperty("type");
        if (type.ValueKind == JsonValueKind.String)
        {
            Assert.Equal(expectedType, type.GetString());
            return;
        }

        Assert.Contains(
            expectedType,
            type.EnumerateArray().Select(value => value.GetString()));
    }

    private static string NewIdempotencyKey() => Guid.NewGuid().ToString("D");
}

internal sealed class FixedCatalogCapability(
    bool isActive,
    bool isAvailable,
    bool requiresPreparation,
    decimal price,
    Guid? preparationResponsibilityId = null) : IOrderConfirmationCatalog
{
    internal bool ObservedActiveTransaction { get; private set; }

    public async Task<IReadOnlyList<OrderConfirmationCatalogProduct>> ReadProductsAsync(
        IReadOnlyCollection<Guid> productIds,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        Assert.NotNull(transaction.Connection);
        Assert.Equal(ConnectionState.Open, transaction.Connection.State);
        await using var command = transaction.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT txid_current()";
        Assert.NotNull(await command.ExecuteScalarAsync(cancellationToken));
        ObservedActiveTransaction = true;

        return productIds.Select(productId => new OrderConfirmationCatalogProduct(
            productId,
            price,
            isActive,
            isAvailable,
            requiresPreparation,
            preparationResponsibilityId)).ToArray();
    }
}

internal sealed class UnexpectedCatalogCapability : IOrderConfirmationCatalog
{
    internal bool WasCalled { get; private set; }

    public Task<IReadOnlyList<OrderConfirmationCatalogProduct>> ReadProductsAsync(
        IReadOnlyCollection<Guid> productIds,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        WasCalled = true;
        throw new InvalidOperationException(
            "Catalog must not be called for a structurally invalid request.");
    }
}

internal sealed class BlockingCatalogDecorator(
    IOrderConfirmationCatalog inner,
    TaskCompletionSource lockAcquired,
    TaskCompletionSource release) : IOrderConfirmationCatalog
{
    public async Task<IReadOnlyList<OrderConfirmationCatalogProduct>> ReadProductsAsync(
        IReadOnlyCollection<Guid> productIds,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var products = await inner.ReadProductsAsync(
            productIds,
            transaction,
            cancellationToken);
        lockAcquired.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
        return products;
    }
}
