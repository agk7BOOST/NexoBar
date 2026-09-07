using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ConfirmationInstructionApiTests(OrderOperationsApiFixture fixture)
{
    private readonly Dictionary<Guid, Guid> pendingCompositionByConfirmationKey = [];
    [Fact]
    public async Task First_canonicalizes_optional_nullable_instruction_without_losing_text()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        var products = new[]
        {
            await fixture.CreateProductAsync("Omitted", "1", token),
            await fixture.CreateProductAsync("Null", "2", token),
            await fixture.CreateProductAsync("Blank", "3", token),
            await fixture.CreateProductAsync("Text", "4", token)
        };
        foreach (var product in products)
        {
            await fixture.SetProductPreparationAsync(product.Id, responsibility, token);
        }

        using var response = await PostFirstRawAsync(
            "Mesa 7",
            [
                new { productId = products[0].Id, quantity = 1 },
                new { productId = products[1].Id, quantity = 1, instruction = (string?)null },
                new { productId = products[2].Id, quantity = 1, instruction = " \t\r\n " },
                new
                {
                    productId = products[3].Id,
                    quantity = 1,
                    instruction = "  SIN  cebolla!\r\nnota  "
                }
            ],
            Guid.NewGuid(),
            token);

        var confirmed = await ReadFirstAsync(response, token);
        Assert.Null(confirmed.FirstIncorporation.Items.Single(
            item => item.ProductId == products[0].Id).Instruction);
        Assert.Null(confirmed.FirstIncorporation.Items.Single(
            item => item.ProductId == products[1].Id).Instruction);
        Assert.Null(confirmed.FirstIncorporation.Items.Single(
            item => item.ProductId == products[2].Id).Instruction);
        Assert.Equal(
            "SIN  cebolla!\nnota",
            confirmed.FirstIncorporation.Items.Single(
                item => item.ProductId == products[3].Id).Instruction);
    }

    [Fact]
    public async Task First_accepts_same_product_with_distinct_instructions_and_replays_canonical_intent()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var productId = Guid.CreateVersion7();
        var responsibilityId = Guid.CreateVersion7();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            dbContext.Products.Add(new Product(productId, "Papas", 10m));
            await dbContext.SaveChangesAsync(token);
        }
        var catalog = new RecordingCatalogCapability(
            new OrderConfirmationCatalogProduct(
                productId, 10m, true, true, true, responsibilityId));
        await using var application = fixture.CreateApplicationWithCatalog(catalog);
        using var client = await fixture.LoginAsync(
            fixture.DefaultOrderOperationsActor,
            token,
            application);
        var key = Guid.NewGuid();

        using var original = await PostFirstAsync(
            client,
            "Mesa 7",
            key,
            token,
            (productId, 1, " sin tomate "),
            (productId, 2, null),
            (productId, 1, " sin cebolla "));
        var originalBody = await original.Content.ReadAsStringAsync(token);
        var confirmed = await ReadFirstAsync(original, token);

        using var replay = await PostFirstAsync(
            client,
            "Mesa 7",
            key,
            token,
            (productId, 1, "sin cebolla"),
            (productId, 1, "sin tomate"),
            (productId, 2, null));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync(token));

        using var incompatible = await PostFirstAsync(
            client,
            "Mesa 7",
            key,
            token,
            (productId, 1, "sin ajo"));
        await AssertProblemAsync(
            incompatible,
            HttpStatusCode.Conflict,
            "order_operations.first_confirmation.idempotency_key_conflict",
            token);

        Assert.Single(catalog.Calls);
        Assert.Equal([productId], catalog.Calls[0]);
        Assert.Equal(
            new string?[] { null, "sin cebolla", "sin tomate" },
            confirmed.FirstIncorporation.Items.Select(item => item.Instruction).ToArray());
        Assert.All(confirmed.FirstIncorporation.Items,
            item => Assert.Equal("10", item.AppliedPrice));

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            var contents = await dbContext.IncorporationContents.AsNoTracking()
                .OrderBy(content => content.ContentOrdinal)
                .ToArrayAsync(token);
            var commandLines = await dbContext.FirstConfirmationCommandContents.AsNoTracking()
                .OrderBy(content => content.LineOrdinal)
                .ToArrayAsync(token);
            Assert.Equal([1, 2, 3], contents.Select(x => x.ContentOrdinal).ToArray());
            Assert.Equal(
                new string?[] { null, "sin cebolla", "sin tomate" },
                contents.Select(x => x.Instruction).ToArray());
            Assert.Equal(
                contents.Select(x => x.Instruction),
                commandLines.Select(x => x.Instruction));
        }

        var works = await fixture.ReadPreparationWorkAsync(token);
        Assert.Equal(3, works.Count);
        Assert.Equal(
            new string?[] { null, "sin cebolla", "sin tomate" },
            works.OrderBy(work => work.ContentOrdinal)
                .Select(work => work.Instruction).ToArray());

        using var orderResponse = await fixture.Client.GetAsync(
            $"/api/order-operations/orders/{confirmed.OperationalReference}", token);
        var order = Assert.IsType<OrderQueryResponse>(
            await orderResponse.Content.ReadFromJsonAsync<OrderQueryResponse>(token));
        Assert.Equal(
            new string?[] { null, "sin cebolla", "sin tomate" },
            Assert.Single(order.Incorporations).Items
                .Select(item => item.Instruction).ToArray());

        var actor = await fixture.CreatePreparationActorAsync(
            hasPreparation: true,
            responsibilityId,
            token);
        using var preparationClient = await fixture.LoginAsync(actor, token);
        using var workResponse = await preparationClient.GetAsync(
            "/api/order-operations/preparation/work" +
            $"?preparationResponsibilityId={responsibilityId:D}", token);
        var queriedWork = Assert.IsType<PreparationWorkResponse[]>(
            await workResponse.Content.ReadFromJsonAsync<PreparationWorkResponse[]>(token));
        Assert.Equal(
            new string?[] { null, "sin cebolla", "sin tomate" },
            queriedWork.Select(work => work.Instruction).ToArray());
        Assert.All(queriedWork, work => Assert.Equal(productId, work.ProductId));
        Assert.Equal(3, queriedWork.Select(work =>
            (work.IncorporationId, work.ContentOrdinal)).Distinct().Count());
        Assert.All(queriedWork, work =>
        {
            var persisted = Assert.Single(works, candidate => candidate.Id == work.WorkId);
            Assert.Equal(persisted.IncorporationId, work.IncorporationId);
            Assert.Equal(persisted.ContentOrdinal, work.ContentOrdinal);
            Assert.Equal(persisted.Instruction, work.Instruction);
            Assert.Equal(persisted.PreparationResponsibilityId, work.PreparationResponsibilityId);
            Assert.Equal(persisted.TotalQuantity, work.TotalQuantity);
            Assert.Equal(persisted.PendingQuantity, work.PendingQuantity);
            Assert.Equal(persisted.InPreparationQuantity, work.InPreparationQuantity);
            Assert.Equal(persisted.ReadyQuantity, work.ReadyQuantity);
        });
    }

    [Fact]
    public async Task Duplicate_canonical_line_is_rejected_before_catalog()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var productId = Guid.CreateVersion7();
        var catalog = new UnexpectedCatalogCapability();
        await using var application = fixture.CreateApplicationWithCatalog(catalog);
        using var client = await fixture.LoginAsync(
            fixture.DefaultOrderOperationsActor,
            token,
            application);

        using var response = await PostFirstAsync(
            client,
            "Mesa 7",
            Guid.NewGuid(),
            token,
            (productId, 1, "sin cebolla"),
            (productId, 2, " sin cebolla "));

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.confirmation.duplicate_line",
            token);
        Assert.False(catalog.WasCalled);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(token));
    }

    [Fact]
    public async Task Non_prepared_product_with_instruction_conflicts_without_effects()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "3", token);

        using var response = await PostFirstAsync(
            fixture.OrderOperationsClient,
            "Mesa 7",
            Guid.NewGuid(),
            token,
            (product.Id, 1, "sin hielo"));

        await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.confirmation.instruction_requires_preparation",
            token);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(token));
        Assert.Equal(0, await fixture.CountPreparationWorkAsync(token));
    }

    [Fact]
    public async Task Subsequent_supports_multiple_homogeneous_lines_and_idempotency()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Hamburguesa", "10", token);
        using var firstResponse = await PostFirstAsync(
            fixture.OrderOperationsClient, "Mesa 7", Guid.NewGuid(), token, (product.Id, 1, null));
        var first = await ReadFirstAsync(firstResponse, token);
        await fixture.SetProductPreparationAsync(product.Id, Guid.CreateVersion7(), token);
        var key = Guid.NewGuid();

        using var original = await PostSubsequentAsync(
            first.OperationalReference,
            key,
            token,
            (product.Id, 1, " sin tomate "),
            (product.Id, 2, "sin cebolla"));
        var originalBody = await original.Content.ReadAsStringAsync(token);
        var confirmed = await ReadSubsequentAsync(original, token);
        using var replay = await PostSubsequentAsync(
            first.OperationalReference,
            key,
            token,
            (product.Id, 2, " sin cebolla "),
            (product.Id, 1, "sin tomate"));
        using var incompatible = await PostSubsequentAsync(
            first.OperationalReference,
            key,
            token,
            (product.Id, 2, "sin cebolla"),
            (product.Id, 1, "sin ajo"));

        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync(token));
        await AssertProblemAsync(
            incompatible,
            HttpStatusCode.Conflict,
            "order_operations.subsequent_confirmation.idempotency_key_conflict",
            token);
        Assert.Equal(
            new[] { "sin cebolla", "sin tomate" },
            confirmed.Incorporation.Items.Select(item => item.Instruction).ToArray());
        Assert.Equal(2, (await fixture.ReadPreparationWorkAsync(token))
            .Count(work => work.IncorporationId == confirmed.Incorporation.Id));
    }

    [Fact]
    public async Task True_to_false_configuration_is_serialized_against_instruction_confirmation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Hamburguesa", "10", token);
        var responsibility = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(product.Id, responsibility, token);
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
        using var client = await fixture.LoginAsync(
            fixture.DefaultOrderOperationsActor,
            token,
            application);

        var confirmationTask = PostFirstAsync(
            client,
            "Mesa 7",
            Guid.NewGuid(),
            token,
            (product.Id, 1, "sin cebolla"));
        try
        {
            await catalogLocked.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var updateTask = PostPreparationChangeAsync(
                client,
                product.Id,
                responsibility,
                null,
                token);
            Assert.True(await fixture.WaitForPriceUpdateLockAsync(
                TimeSpan.FromSeconds(10), token));
            releaseCatalog.TrySetResult();

            using var confirmation = await confirmationTask.WaitAsync(
                TimeSpan.FromSeconds(10), token);
            using var update = await updateTask.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
            Assert.Equal("sin cebolla", work.Instruction);
            Assert.Equal(responsibility, work.PreparationResponsibilityId);
        }
        finally
        {
            releaseCatalog.TrySetResult();
        }

        await fixture.ResetAsync(token);
        var secondProduct = await fixture.CreateProductAsync("Pizza", "12", token);
        var secondResponsibility = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(
            secondProduct.Id,
            secondResponsibility,
            token);
        using var disable = await PostPreparationChangeAsync(
            fixture.OrderOperationsClient,
            secondProduct.Id,
            secondResponsibility,
            null,
            token);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        using var rejected = await PostFirstAsync(
            fixture.OrderOperationsClient,
            "Mesa 8",
            Guid.NewGuid(),
            token,
            (secondProduct.Id, 1, "sin queso"));
        await AssertProblemAsync(
            rejected,
            HttpStatusCode.Conflict,
            "order_operations.confirmation.instruction_requires_preparation",
            token);
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(token));
    }

    private async Task<HttpResponseMessage> PostFirstRawAsync(
        string context,
        object[] items,
        Guid key,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new { context, items })
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient,
            request,
            token);
    }

    private static async Task<HttpResponseMessage> PostFirstAsync(
        HttpClient client,
        string context,
        Guid key,
        CancellationToken token,
        params (Guid ProductId, int Quantity, string? Instruction)[] items)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                context,
                items.Select(item => new FirstConfirmationItemRequest(
                    item.ProductId,
                    item.Quantity,
                    item.Instruction)).ToArray()))
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            client,
            request,
            token);
    }

    private async Task<HttpResponseMessage> PostSubsequentAsync(
        string operationalReference,
        Guid key,
        CancellationToken token,
        params (Guid ProductId, int Quantity, string? Instruction)[] items)
    {
        if (!pendingCompositionByConfirmationKey.TryGetValue(
                key,
                out var pendingCompositionId))
        {
            pendingCompositionId = (await fixture.StartPendingCompositionAsync(
                operationalReference,
                token)).PendingCompositionId;
            pendingCompositionByConfirmationKey[key] = pendingCompositionId;
        }
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{operationalReference}/confirmations")
        {
            Content = JsonContent.Create(new SubsequentConfirmationRequest(
                pendingCompositionId,
                items.Select(item => new SubsequentConfirmationItemRequest(
                    item.ProductId,
                    item.Quantity,
                    item.Instruction)).ToArray()))
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient,
            request,
            token);
    }

    private static async Task<HttpResponseMessage> PostPreparationChangeAsync(
        HttpClient client,
        Guid productId,
        Guid? expected,
        Guid? next,
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
        HttpStatusCode status,
        string code,
        CancellationToken token)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(token),
            cancellationToken: token);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }
}

internal sealed class RecordingCatalogCapability(
    params OrderConfirmationCatalogProduct[] products) : IOrderConfirmationCatalog
{
    private readonly IReadOnlyDictionary<Guid, OrderConfirmationCatalogProduct> productsById =
        products.ToDictionary(product => product.ProductId);

    internal List<Guid[]> Calls { get; } = [];

    public Task<IReadOnlyList<OrderConfirmationCatalogProduct>> ReadProductsAsync(
        IReadOnlyCollection<Guid> productIds,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.NotNull(transaction.Connection);
        Calls.Add(productIds.ToArray());
        return Task.FromResult<IReadOnlyList<OrderConfirmationCatalogProduct>>(
            productIds.Select(productId => productsById[productId]).ToArray());
    }
}
