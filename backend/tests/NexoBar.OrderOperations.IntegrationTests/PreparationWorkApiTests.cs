using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.OperationalConfiguration;
using NexoBar.OrderOperations;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PreparationWorkApiTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Mixed_first_confirmation_creates_content_and_work_only_for_prepared_products()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var kitchen = Guid.CreateVersion7();
        var bar = Guid.CreateVersion7();
        var preparedKitchen = await fixture.CreateProductAsync("Papas", "5", token);
        var plain = await fixture.CreateProductAsync("Agua", "3", token);
        var preparedBar = await fixture.CreateProductAsync("Trago", "8", token);
        await fixture.SetProductPreparationAsync(preparedKitchen.Id, kitchen, token);
        await fixture.SetProductPreparationAsync(preparedBar.Id, bar, token);

        using var response = await PostFirstAsync(
            fixture.Client,
            "Mesa 7",
            Guid.NewGuid(),
            token,
            (preparedKitchen.Id, 2),
            (plain.Id, 3),
            (preparedBar.Id, 4));
        var confirmed = await ReadFirstAsync(response, token);

        Assert.Equal(3, confirmed.FirstIncorporation.Items.Count);
        var works = await fixture.ReadPreparationWorkAsync(token);
        Assert.Equal(2, works.Count);
        Assert.DoesNotContain(works, work => work.ProductId == plain.Id);
        AssertWork(
            Assert.Single(works, work => work.ProductId == preparedKitchen.Id),
            confirmed.FirstIncorporation.Id,
            kitchen,
            2);
        AssertWork(
            Assert.Single(works, work => work.ProductId == preparedBar.Id),
            confirmed.FirstIncorporation.Id,
            bar,
            4);
        Assert.Equal(new PersistenceCounts(1, 1, 3, 1, 1, 3),
            await fixture.CountEffectsAsync(token));
    }

    [Fact]
    public async Task Destination_snapshot_is_prospective_across_confirmations()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(product.Id, a, token);

        using var firstResponse = await PostFirstAsync(
            fixture.Client, "Mesa 7", Guid.NewGuid(), token, (product.Id, 2));
        var first = await ReadFirstAsync(firstResponse, token);
        Assert.Equal(a, Assert.Single(
            await fixture.ReadPreparationWorkAsync(token)).PreparationResponsibilityId);

        await fixture.SetProductPreparationAsync(product.Id, b, token);
        Assert.Equal(a, Assert.Single(
            await fixture.ReadPreparationWorkAsync(token)).PreparationResponsibilityId);

        using var subsequentResponse = await PostSubsequentAsync(
            fixture.Client,
            first.OperationalReference,
            Guid.NewGuid(),
            token,
            (product.Id, 3));
        var subsequent = await ReadSubsequentAsync(subsequentResponse, token);
        var works = await fixture.ReadPreparationWorkAsync(token);

        Assert.Equal(a, Assert.Single(
            works, work => work.IncorporationId == first.FirstIncorporation.Id)
            .PreparationResponsibilityId);
        Assert.Equal(b, Assert.Single(
            works, work => work.IncorporationId == subsequent.Incorporation.Id)
            .PreparationResponsibilityId);
    }

    [Fact]
    public async Task First_replay_does_not_call_catalog_or_duplicate_work()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        await fixture.SetProductPreparationAsync(product.Id, Guid.CreateVersion7(), token);
        var key = Guid.NewGuid();

        using var first = await PostFirstAsync(
            fixture.Client, "Mesa 7", key, token, (product.Id, 2));
        var originalBody = await first.Content.ReadAsStringAsync(token);
        var unexpectedCatalog = new UnexpectedCatalogCapability();
        await using var application = fixture.CreateApplicationWithCatalog(unexpectedCatalog);
        using var client = application.CreateClient();
        using var replay = await PostFirstAsync(
            client, "Mesa 7", key, token, (product.Id, 2));

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync(token));
        Assert.False(unexpectedCatalog.WasCalled);
        Assert.Equal(1, await fixture.CountPreparationWorkAsync(token));
    }

    [Fact]
    public async Task Incoherent_catalog_preparation_snapshot_is_an_internal_failure()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var productId = Guid.CreateVersion7();

        foreach (var catalog in new IOrderConfirmationCatalog[]
                 {
                     new FixedCatalogCapability(true, true, true, 5m),
                     new FixedCatalogCapability(
                         true,
                         true,
                         false,
                         5m,
                         Guid.CreateVersion7())
                 })
        {
            await using var application = fixture.CreateApplicationWithCatalog(catalog);
            using var client = application.CreateClient();
            using var response = await PostFirstAsync(
                client, "Mesa 7", Guid.NewGuid(), token, (productId, 1));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(token));
            Assert.Equal(0, await fixture.CountPreparationWorkAsync(token));
        }
    }

    [Fact]
    public async Task Preparation_query_filters_and_uses_current_order_context()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        var otherResponsibility = Guid.CreateVersion7();
        var firstProduct = await fixture.CreateProductAsync("Papas", "5", token);
        var secondProduct = await fixture.CreateProductAsync("Pizza", "9", token);
        var otherProduct = await fixture.CreateProductAsync("Trago", "8", token);
        await fixture.SetProductPreparationAsync(firstProduct.Id, responsibility, token);
        await fixture.SetProductPreparationAsync(secondProduct.Id, responsibility, token);
        await fixture.SetProductPreparationAsync(otherProduct.Id, otherResponsibility, token);

        using var firstResponse = await PostFirstAsync(
            fixture.Client,
            "Mesa histórica",
            Guid.NewGuid(),
            token,
            (secondProduct.Id, 2),
            (firstProduct.Id, 1),
            (otherProduct.Id, 3));
        var first = await ReadFirstAsync(firstResponse, token);
        var orderId = Guid.Parse(first.OperationalReference);
        await fixture.SetOrderContextAsync(orderId, "Mesa vigente", token);

        using var query = await fixture.Client.GetAsync(
            $"/api/order-operations/preparation/work?preparationResponsibilityId={responsibility:D}",
            token);
        query.EnsureSuccessStatusCode();
        var result = Assert.IsType<PreparationWorkResponse[]>(
            await query.Content.ReadFromJsonAsync<PreparationWorkResponse[]>(token));

        Assert.Equal(2, result.Length);
        Assert.All(result, work =>
        {
            Assert.Equal(responsibility, work.PreparationResponsibilityId);
            Assert.Equal(first.OperationalReference, work.OperationalReference);
            Assert.Equal("Mesa vigente", work.Context);
            Assert.Equal(first.FirstIncorporation.Id, work.IncorporationId);
            Assert.Equal(1, work.IncorporationOrdinal);
            Assert.Equal(first.FirstIncorporation.ConfirmedAt, work.ConfirmedAt);
            Assert.Equal(work.TotalQuantity, work.PendingQuantity);
            Assert.Equal(0, work.InPreparationQuantity);
            Assert.Equal(0, work.ReadyQuantity);
        });
        Assert.Equal(
            result.OrderBy(work => work.ConfirmedAt)
                .ThenBy(work => work.IncorporationId)
                .ThenBy(work => work.ProductId)
                .ThenBy(work => work.WorkId),
            result);
        var snapshot = await fixture.ReadSnapshotAsync(token);
        Assert.Equal("Mesa histórica", snapshot.History.ConfirmedContext);
    }

    [Fact]
    public async Task Preparation_query_validates_only_filter_structure()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        using var missing = await fixture.Client.GetAsync(
            "/api/order-operations/preparation/work",
            token);
        await AssertProblemAsync(
            missing,
            "order_operations.preparation_work.responsibility_id_required",
            token);

        using var malformed = await fixture.Client.GetAsync(
            "/api/order-operations/preparation/work?preparationResponsibilityId=not-a-uuid",
            token);
        await AssertProblemAsync(
            malformed,
            "order_operations.preparation_work.responsibility_id_invalid",
            token);

        using var unknown = await fixture.Client.GetAsync(
            $"/api/order-operations/preparation/work?preparationResponsibilityId={Guid.CreateVersion7():D}",
            token);
        unknown.EnsureSuccessStatusCode();
        Assert.Empty(Assert.IsType<PreparationWorkResponse[]>(
            await unknown.Content.ReadFromJsonAsync<PreparationWorkResponse[]>(token)));
    }

    [Fact]
    public async Task Preparation_configuration_update_waits_and_work_keeps_stabilized_destination()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(product.Id, a, token);
        var catalogLocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCatalog = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application =
            fixture.CreateApplicationWithCatalogDecoratorAndPreparationLookup(
                services => new BlockingCatalogDecorator(
                    new OrderConfirmationCatalog(
                        services.GetRequiredService<CatalogDbContext>()),
                    catalogLocked,
                    releaseCatalog),
                new ExistingPreparationResponsibilityLookup());
        using var client = application.CreateClient();

        var confirmationTask = PostFirstAsync(
            client, "Mesa 7", Guid.NewGuid(), token, (product.Id, 2));
        try
        {
            await catalogLocked.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var updateTask = PostPreparationConfigurationChangeAsync(
                client, product.Id, a, b, token);
            Assert.True(await fixture.WaitForPriceUpdateLockAsync(
                TimeSpan.FromSeconds(10), token));
            releaseCatalog.TrySetResult();

            using var confirmation = await confirmationTask.WaitAsync(
                TimeSpan.FromSeconds(10), token);
            using var update = await updateTask.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            Assert.Equal(a, Assert.Single(
                await fixture.ReadPreparationWorkAsync(token))
                .PreparationResponsibilityId);
            var current = await client.GetFromJsonAsync<ProductResponse>(
                $"/api/catalog/products/{product.Id:D}", token);
            Assert.Equal(b, Assert.IsType<ProductResponse>(current).PreparationResponsibilityId);
        }
        finally
        {
            releaseCatalog.TrySetResult();
        }
    }

    [Fact]
    public async Task Preparation_work_failure_rolls_back_first_and_allows_same_key_retry()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        await fixture.SetProductPreparationAsync(product.Id, Guid.CreateVersion7(), token);
        var key = Guid.NewGuid();
        await fixture.SetPreparationWorkFailureAsync(true, token);

        try
        {
            using var failed = await PostFirstAsync(
                fixture.Client, "Mesa 7", key, token, (product.Id, 2));
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(token));
            Assert.Equal(0, await fixture.CountPreparationWorkAsync(token));
        }
        finally
        {
            await fixture.SetPreparationWorkFailureAsync(false, token);
        }

        using var retry = await PostFirstAsync(
            fixture.Client, "Mesa 7", key, token, (product.Id, 2));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, await fixture.CountPreparationWorkAsync(token));
    }

    [Fact]
    public async Task Preparation_work_failure_rolls_back_only_subsequent_effects()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        using var firstResponse = await PostFirstAsync(
            fixture.Client, "Mesa 7", Guid.NewGuid(), token, (product.Id, 1));
        var first = await ReadFirstAsync(firstResponse, token);
        await fixture.SetProductPreparationAsync(product.Id, Guid.CreateVersion7(), token);
        await fixture.SetPreparationWorkFailureAsync(true, token);

        try
        {
            using var failed = await PostSubsequentAsync(
                fixture.Client,
                first.OperationalReference,
                Guid.NewGuid(),
                token,
                (product.Id, 2));
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(new SubsequentPersistenceCounts(1, 1, 1, 0, 0),
                await fixture.CountSubsequentEffectsAsync(token));
            Assert.Equal(0, await fixture.CountPreparationWorkAsync(token));
        }
        finally
        {
            await fixture.SetPreparationWorkFailureAsync(false, token);
        }
    }

    [Fact]
    public async Task Database_enforces_preparation_work_constraints_and_content_identity()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "3", token);
        using var firstResponse = await PostFirstAsync(
            fixture.Client, "Mesa 7", Guid.NewGuid(), token, (product.Id, 1));
        var first = await ReadFirstAsync(firstResponse, token);
        var responsibility = Guid.CreateVersion7();

        foreach (var quantities in new[]
                 {
                     (Total: 0, Pending: 0, Preparing: 0, Ready: 0),
                     (Total: 1, Pending: -1, Preparing: 0, Ready: 2),
                     (Total: 1, Pending: 0, Preparing: -1, Ready: 2),
                     (Total: 1, Pending: 0, Preparing: 2, Ready: -1),
                     (Total: 2, Pending: 1, Preparing: 0, Ready: 0)
                 })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertWorkAsync(
                    Guid.CreateVersion7(),
                    first.FirstIncorporation.Id,
                    1,
                    responsibility,
                    quantities.Total,
                    quantities.Pending,
                    quantities.Preparing,
                    quantities.Ready,
                    token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        var orphan = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertWorkAsync(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                1,
                responsibility,
                1,
                1,
                0,
                0,
                token));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState);

        await InsertWorkAsync(
            Guid.CreateVersion7(),
            first.FirstIncorporation.Id,
            1,
            responsibility,
            1,
            1,
            0,
            0,
            token);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertWorkAsync(
                Guid.CreateVersion7(),
                first.FirstIncorporation.Id,
                1,
                responsibility,
                1,
                1,
                0,
                0,
                token));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [Fact]
    public async Task OpenApi_describes_required_preparation_work_query_contract()
    {
        var token = TestContext.Current.CancellationToken;
        using var response = await fixture.Client.GetAsync("/openapi/v1.json", token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(token),
            cancellationToken: token);
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/order-operations/preparation/work")
            .GetProperty("get");
        var parameter = Assert.Single(operation.GetProperty("parameters")
            .EnumerateArray());
        Assert.Equal("preparationResponsibilityId",
            parameter.GetProperty("name").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
        Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("400", out _));

        var schema = document.RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(nameof(PreparationWorkResponse));
        var operationalReference = schema.GetProperty("properties")
            .GetProperty("operationalReference");
        Assert.Equal("string", operationalReference.GetProperty("type").GetString());
        Assert.False(operationalReference.TryGetProperty("format", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("context", out _));
    }

    private async Task InsertWorkAsync(
        Guid id,
        Guid incorporationId,
        int contentOrdinal,
        Guid responsibilityId,
        int total,
        int pending,
        int preparing,
        int ready,
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO order_operations.preparation_work
                (id, incorporation_id, content_ordinal, preparation_responsibility_id,
                 total_quantity, pending_quantity, in_preparation_quantity, ready_quantity)
            VALUES
                (@id, @incorporationId, @contentOrdinal, @responsibilityId,
                 @total, @pending, @preparing, @ready)
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("incorporationId", incorporationId);
        command.Parameters.AddWithValue("contentOrdinal", contentOrdinal);
        command.Parameters.AddWithValue("responsibilityId", responsibilityId);
        command.Parameters.AddWithValue("total", total);
        command.Parameters.AddWithValue("pending", pending);
        command.Parameters.AddWithValue("preparing", preparing);
        command.Parameters.AddWithValue("ready", ready);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<HttpResponseMessage> PostFirstAsync(
        HttpClient client,
        string context,
        Guid key,
        CancellationToken token,
        params (Guid ProductId, int Quantity)[] items)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                context,
                items.Select(item => new FirstConfirmationItemRequest(
                    item.ProductId,
                    item.Quantity)).ToArray()))
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await client.SendAsync(request, token);
    }

    private static async Task<HttpResponseMessage> PostSubsequentAsync(
        HttpClient client,
        string operationalReference,
        Guid key,
        CancellationToken token,
        params (Guid ProductId, int Quantity)[] items)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{operationalReference}/confirmations")
        {
            Content = JsonContent.Create(new SubsequentConfirmationRequest(
                items.Select(item => new SubsequentConfirmationItemRequest(
                    item.ProductId,
                    item.Quantity)).ToArray()))
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await client.SendAsync(request, token);
    }

    private static async Task<HttpResponseMessage> PostPreparationConfigurationChangeAsync(
        HttpClient client,
        Guid productId,
        Guid expected,
        Guid next,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/catalog/products/{productId:D}/preparation-configuration-changes")
        {
            Content = JsonContent.Create(new
            {
                expectedCurrentPreparationResponsibilityId = expected,
                newPreparationResponsibilityId = next
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await client.SendAsync(request, token);
    }

    private static async Task<FirstConfirmationResponse> ReadFirstAsync(
        HttpResponseMessage response,
        CancellationToken token)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
    }

    private static async Task<SubsequentConfirmationResponse> ReadSubsequentAsync(
        HttpResponseMessage response,
        CancellationToken token)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<SubsequentConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<SubsequentConfirmationResponse>(token));
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        string code,
        CancellationToken token)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(token),
            cancellationToken: token);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static void AssertWork(
        PreparationWorkSnapshot work,
        Guid incorporationId,
        Guid responsibilityId,
        int quantity)
    {
        Assert.Equal(7, work.Id.ToByteArray(bigEndian: true)[6] >> 4);
        Assert.Equal(incorporationId, work.IncorporationId);
        Assert.Equal(responsibilityId, work.PreparationResponsibilityId);
        Assert.Equal(quantity, work.TotalQuantity);
        Assert.Equal(quantity, work.PendingQuantity);
        Assert.Equal(0, work.InPreparationQuantity);
        Assert.Equal(0, work.ReadyQuantity);
    }
}

internal sealed class ExistingPreparationResponsibilityLookup :
    IPreparationResponsibilityLookup
{
    public Task<bool> ExistsAsync(
        Guid responsibilityId,
        CancellationToken cancellationToken) => Task.FromResult(true);
}
