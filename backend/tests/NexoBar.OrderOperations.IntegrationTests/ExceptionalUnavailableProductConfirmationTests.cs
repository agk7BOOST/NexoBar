using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ExceptionalUnavailableProductConfirmationTests(
    OrderOperationsApiFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_exception_applies_per_line_with_current_price_preparation_and_reload_audit()
    {
        await fixture.ResetAsync(Token);
        var available = await fixture.CreateProductAsync("Available", "11.25", Token);
        var unavailable = await fixture.CreateProductAsync("Unavailable", "23.50", Token);
        var secondUnavailable = await fixture.CreateProductAsync(
            "Second unavailable", "31", Token);
        var availableExceptional = await fixture.CreateProductAsync(
            "Available exceptional intent", "7", Token);
        var destination = Guid.NewGuid();
        await fixture.SetProductPreparationAsync(unavailable.Id, destination, Token);
        await fixture.SetProductStateAsync(unavailable.Id, true, false, Token);
        await fixture.SetProductStateAsync(secondUnavailable.Id, true, false, Token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var client = await fixture.LoginAsync(actor, Token);
        var request = new FirstConfirmationRequest("Mesa S10",
        [
            new(available.Id, 2),
            new(unavailable.Id, 3, "sin hielo", true),
            new(secondUnavailable.Id, 1, null, true),
            new(availableExceptional.Id, 1, null, true)
        ]);

        using var response = await PostFirstAsync(client, request, Guid.NewGuid());
        var confirmation = await ReadFirstAsync(response);
        Assert.Equal(4, confirmation.FirstIncorporation.Items.Count);
        Assert.False(confirmation.FirstIncorporation.Items.Single(item =>
            item.ProductId == available.Id).UnavailableProductExceptionApplied);
        var applied = confirmation.FirstIncorporation.Items.Single(item =>
            item.ProductId == unavailable.Id);
        Assert.True(applied.UnavailableProductExceptionApplied);
        Assert.Equal("23.50", applied.AppliedPrice);
        Assert.True(confirmation.FirstIncorporation.Items.Single(item =>
            item.ProductId == secondUnavailable.Id).UnavailableProductExceptionApplied);
        Assert.False(confirmation.FirstIncorporation.Items.Single(item =>
            item.ProductId == availableExceptional.Id).UnavailableProductExceptionApplied);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var orderOperations = scope.ServiceProvider
                .GetRequiredService<OrderOperationsDbContext>();
            var contents = await orderOperations.IncorporationContents.AsNoTracking()
                .OrderBy(content => content.ContentOrdinal)
                .ToArrayAsync(Token);
            Assert.True(contents.Single(content => content.ProductId == unavailable.Id)
                .UnavailableProductExceptionApplied);
            Assert.False(contents.Single(content => content.ProductId == available.Id)
                .UnavailableProductExceptionApplied);
            Assert.False(contents.Single(content => content.ProductId == availableExceptional.Id)
                .UnavailableProductExceptionApplied);
            var commandContents = await orderOperations.FirstConfirmationCommandContents
                .AsNoTracking().ToArrayAsync(Token);
            Assert.True(commandContents.Single(content => content.ProductId == unavailable.Id)
                .IntentUnavailableProductExceptionRequested);
            Assert.True(commandContents.Single(content => content.ProductId == secondUnavailable.Id)
                .IntentUnavailableProductExceptionRequested);
            Assert.True(commandContents.Single(content => content.ProductId == availableExceptional.Id)
                .IntentUnavailableProductExceptionRequested);
            Assert.False(commandContents.Single(content => content.ProductId == available.Id)
                .IntentUnavailableProductExceptionRequested);
            Assert.Single(await orderOperations.PreparationWork.AsNoTracking().ToArrayAsync(Token));

            var catalogDbContext = scope.ServiceProvider
                .GetRequiredService<CatalogDbContext>();
            await using var transaction = await catalogDbContext.Database
                .BeginTransactionAsync(Token);
            var catalogSnapshot = Assert.Single(await scope.ServiceProvider
                .GetRequiredService<IOrderConfirmationCatalog>()
                .ReadProductsAsync(
                    [unavailable.Id],
                    transaction.GetDbTransaction(),
                    Token));
            Assert.False(catalogSnapshot.IsAvailable);
        }

        using var read = await client.GetAsync(
            $"/api/order-operations/orders/{confirmation.OperationalReference}", Token);
        read.EnsureSuccessStatusCode();
        var order = Assert.IsType<OrderQueryResponse>(
            await read.Content.ReadFromJsonAsync<OrderQueryResponse>(Token));
        Assert.True(order.Incorporations.Single().Items.Single(item =>
            item.ProductId == unavailable.Id).UnavailableProductExceptionApplied);
    }

    [Fact]
    public async Task Exception_marker_remains_historical_after_availability_is_restored()
    {
        await fixture.ResetAsync(Token);
        var unavailable = await fixture.CreateProductAsync("Historical unavailable", "19", Token);
        var destination = Guid.NewGuid();
        await fixture.SetProductPreparationAsync(unavailable.Id, destination, Token);
        await fixture.SetProductStateAsync(unavailable.Id, true, false, Token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var client = await fixture.LoginAsync(actor, Token);

        using var confirmationResponse = await PostFirstAsync(
            client,
            FirstRequest(unavailable.Id, exceptional: true),
            Guid.NewGuid());
        var confirmation = await ReadFirstAsync(confirmationResponse);
        Assert.True(Assert.Single(confirmation.FirstIncorporation.Items)
            .UnavailableProductExceptionApplied);

        using var availabilityRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/catalog/products/{unavailable.Id:D}/availability-changes")
        {
            Content = JsonContent.Create(new
            {
                expectedCurrentAvailability = false,
                newAvailability = true
            })
        };
        availabilityRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var availabilityResponse = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            client, availabilityRequest, Token);
        availabilityResponse.EnsureSuccessStatusCode();

        using var orderResponse = await client.GetAsync(
            $"/api/order-operations/orders/{confirmation.OperationalReference}", Token);
        orderResponse.EnsureSuccessStatusCode();
        var order = Assert.IsType<OrderQueryResponse>(
            await orderResponse.Content.ReadFromJsonAsync<OrderQueryResponse>(Token));
        var item = order.Incorporations.Single().Items.Single(item =>
            item.ProductId == unavailable.Id);
        Assert.True(item.UnavailableProductExceptionApplied);
        Assert.Equal("19", item.AppliedPrice);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var work = Assert.Single(await db.PreparationWork.AsNoTracking()
            .Where(candidate => candidate.IncorporationId == confirmation.FirstIncorporation.Id)
            .ToArrayAsync(Token));
        Assert.Equal(destination, work.PreparationResponsibilityId);
    }

    [Fact]
    public async Task First_authority_matrix_and_ordinary_unavailable_contract_are_stable()
    {
        await fixture.ResetAsync(Token);
        var product = await fixture.CreateProductAsync("Unavailable", "10", Token);
        await fixture.SetProductStateAsync(product.Id, true, false, Token);

        foreach (var (hasOperations, hasIntervention, expectedCode) in new[]
        {
            (true, false, "order_operations.confirmation.operational_intervention_required"),
            (false, true, "order_operations.confirmation.forbidden"),
            (false, false, "order_operations.confirmation.forbidden")
        })
        {
            var actor = await fixture.CreateConfirmationActorAsync(
                hasOperations, hasIntervention, Token);
            using var client = await fixture.LoginAsync(actor, Token);
            using var response = await PostFirstAsync(client,
                FirstRequest(product.Id, exceptional: true), Guid.NewGuid());
            await AssertProblemAsync(response, HttpStatusCode.Forbidden, expectedCode);
        }

        var dual = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using (var client = await fixture.LoginAsync(dual, Token))
        using (var response = await PostFirstAsync(client,
            new
            {
                context = "Mesa S10",
                items = new[] { new { productId = product.Id, quantity = 1 } }
            },
            Guid.NewGuid()))
            await AssertProblemAsync(response, HttpStatusCode.Conflict,
                "order_operations.first_confirmation.product_unavailable");

        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(Token));
    }

    [Fact]
    public async Task First_mixed_ordinary_unavailable_line_rejects_atomically_without_leaking_exception()
    {
        await fixture.ResetAsync(Token);
        var available = await fixture.CreateProductAsync("Available", "5", Token);
        var unavailable = await fixture.CreateProductAsync("Unavailable", "6", Token);
        await fixture.SetProductPreparationAsync(unavailable.Id, Guid.NewGuid(), Token);
        await fixture.SetProductStateAsync(unavailable.Id, true, false, Token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var client = await fixture.LoginAsync(actor, Token);

        var requests = new[]
        {
            new FirstConfirmationRequest("Mixed", [new(available.Id, 1), new(unavailable.Id, 1)]),
            new FirstConfirmationRequest("Mixed", [new(unavailable.Id, 1, "A", true), new(unavailable.Id, 1, "B")])
        };
        foreach (var request in requests)
        {
            using var response = await PostFirstAsync(client, request, Guid.NewGuid());
            await AssertProblemAsync(response, HttpStatusCode.Conflict,
                "order_operations.first_confirmation.product_unavailable");
        }

        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(Token));
    }

    [Fact]
    public async Task First_exact_exceptional_replay_bypasses_revoked_authority_and_changed_intent_conflicts()
    {
        await fixture.ResetAsync(Token);
        var product = await fixture.CreateProductAsync("Unavailable", "19", Token);
        await fixture.SetProductStateAsync(product.Id, true, false, Token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var client = await fixture.LoginAsync(actor, Token);
        var key = Guid.NewGuid();
        var request = FirstRequest(product.Id, exceptional: true);

        using var original = await PostFirstAsync(client, request, key);
        var originalBody = await original.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        var before = await fixture.CountEffectsAsync(Token);
        var otherActor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using (var otherClient = await fixture.LoginAsync(otherActor, Token))
        using (var actorMismatch = await PostFirstAsync(otherClient, request, key))
            await AssertProblemAsync(actorMismatch, HttpStatusCode.Conflict,
                "order_operations.first_confirmation.idempotency_key_conflict");
        await fixture.RevokeOperationalInterventionAssignmentAsync(actor.IdentityId, Token);
        await fixture.SetProductStateAsync(product.Id, false, false, Token);

        using (var replay = await PostFirstAsync(client, request, key))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync(Token));
        }
        Assert.Equal(before, await fixture.CountEffectsAsync(Token));

        using (var changedIntent = await PostFirstAsync(client,
            FirstRequest(product.Id, exceptional: false), key))
            await AssertProblemAsync(changedIntent, HttpStatusCode.Conflict,
                "order_operations.first_confirmation.idempotency_key_conflict");

        using (var freshWithoutIntervention = await PostFirstAsync(client, request, Guid.NewGuid()))
            await AssertProblemAsync(freshWithoutIntervention, HttpStatusCode.Forbidden,
                "order_operations.confirmation.operational_intervention_required");

        await fixture.RevokeOrderOperationsAssignmentAsync(actor.IdentityId, Token);
        using (var replayWithoutEitherResponsibility = await PostFirstAsync(
            client, request, key))
        {
            Assert.Equal(HttpStatusCode.Created, replayWithoutEitherResponsibility.StatusCode);
            Assert.Equal(
                originalBody,
                await replayWithoutEitherResponsibility.Content.ReadAsStringAsync(Token));
        }
        Assert.Equal(before, await fixture.CountEffectsAsync(Token));

        using var freshWithoutBase = await PostFirstAsync(client, request, Guid.NewGuid());
        await AssertProblemAsync(freshWithoutBase, HttpStatusCode.Forbidden,
            "order_operations.confirmation.forbidden");
    }

    [Fact]
    public async Task First_same_key_ordinary_to_exceptional_multi_line_intent_conflicts()
    {
        await fixture.ResetAsync(Token);
        var first = await fixture.CreateProductAsync("First", "4", Token);
        var second = await fixture.CreateProductAsync("Second", "5", Token);
        var key = Guid.NewGuid();
        var ordinary = new FirstConfirmationRequest("Mesa S10",
            [new(first.Id, 1), new(second.Id, 1)]);
        using var original = await PostFirstAsync(
            fixture.OrderOperationsClient, ordinary, key);
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        var changed = ordinary with
        {
            Items = [new FirstConfirmationItemRequest(first.Id, 1),
                new FirstConfirmationItemRequest(second.Id, 1, null, true)]
        };

        using var conflict = await PostFirstAsync(
            fixture.OrderOperationsClient, changed, key);
        await AssertProblemAsync(conflict, HttpStatusCode.Conflict,
            "order_operations.first_confirmation.idempotency_key_conflict");
    }

    [Fact]
    public async Task First_exception_cannot_override_non_current_product()
    {
        await fixture.ResetAsync(Token);
        var product = await fixture.CreateProductAsync("Inactive", "4", Token);
        await fixture.SetProductStateAsync(product.Id, false, false, Token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var client = await fixture.LoginAsync(actor, Token);
        using var response = await PostFirstAsync(client,
            FirstRequest(product.Id, exceptional: true), Guid.NewGuid());
        await AssertProblemAsync(response, HttpStatusCode.Conflict,
            "order_operations.confirmation.product_not_current");
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(Token));
    }

    [Fact]
    public async Task Subsequent_exception_succeeds_and_consumes_pending_composition_normally()
    {
        await fixture.ResetAsync(Token);
        var initial = await fixture.CreateProductAsync("Initial", "3", Token);
        var unavailable = await fixture.CreateProductAsync("Unavailable", "17", Token);
        var available = await fixture.CreateProductAsync("Available", "8", Token);
        var destination = Guid.NewGuid();
        await fixture.SetProductPreparationAsync(unavailable.Id, destination, Token);
        var order = await CreateOrderAsync(initial.Id);
        await fixture.SetProductStateAsync(unavailable.Id, true, false, Token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var client = await fixture.LoginAsync(actor, Token);
        var pending = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, Token, client);
        var request = new SubsequentConfirmationRequest(pending.PendingCompositionId,
        [
            new(available.Id, 2),
            new(unavailable.Id, 4, null, true)
        ]);

        using var response = await PostSubsequentAsync(
            client, order.OperationalReference, request, Guid.NewGuid());
        var confirmation = await ReadSubsequentAsync(response);
        var exceptional = confirmation.Incorporation.Items.Single(item =>
            item.ProductId == unavailable.Id);
        Assert.True(exceptional.UnavailableProductExceptionApplied);
        Assert.Equal("17", exceptional.AppliedPrice);

        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Empty(await dbContext.PendingCompositions.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(2, await dbContext.Incorporations.CountAsync(Token));
        Assert.True(await dbContext.IncorporationContents.AsNoTracking().AnyAsync(content =>
            content.IncorporationId == confirmation.Incorporation.Id &&
            content.ProductId == unavailable.Id &&
            content.UnavailableProductExceptionApplied, Token));
        var work = Assert.Single(await dbContext.PreparationWork.AsNoTracking()
            .Where(candidate => candidate.IncorporationId == confirmation.Incorporation.Id)
            .ToArrayAsync(Token));
        Assert.Equal(destination, work.PreparationResponsibilityId);

        using var read = await client.GetAsync(
            $"/api/order-operations/orders/{order.OperationalReference}", Token);
        read.EnsureSuccessStatusCode();
        var reloaded = Assert.IsType<OrderQueryResponse>(
            await read.Content.ReadFromJsonAsync<OrderQueryResponse>(Token));
        Assert.True(reloaded.Incorporations.Single(incorporation =>
                incorporation.Id == confirmation.Incorporation.Id)
            .Items.Single(item => item.ProductId == unavailable.Id)
            .UnavailableProductExceptionApplied);
    }

    [Fact]
    public async Task Subsequent_exceptional_intent_requires_both_authorities_even_when_product_is_available()
    {
        await fixture.ResetAsync(Token);
        var product = await fixture.CreateProductAsync("Available", "12", Token);
        var order = await CreateOrderAsync(product.Id);
        var pending = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, Token);
        var request = new SubsequentConfirmationRequest(pending.PendingCompositionId,
            [new(product.Id, 1, null, true)]);

        var operationsOnly = await fixture.CreateConfirmationActorAsync(true, false, Token);
        using (var client = await fixture.LoginAsync(operationsOnly, Token))
        using (var response = await PostSubsequentAsync(
            client, order.OperationalReference, request, Guid.NewGuid()))
            await AssertProblemAsync(response, HttpStatusCode.Forbidden,
                "order_operations.confirmation.operational_intervention_required");

        var interventionOnly = await fixture.CreateConfirmationActorAsync(false, true, Token);
        using (var client = await fixture.LoginAsync(interventionOnly, Token))
        using (var response = await PostSubsequentAsync(
            client, order.OperationalReference, request, Guid.NewGuid()))
            await AssertProblemAsync(response, HttpStatusCode.Forbidden,
                "order_operations.confirmation.forbidden");

        var dual = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var dualClient = await fixture.LoginAsync(dual, Token);
        using var success = await PostSubsequentAsync(
            dualClient, order.OperationalReference, request, Guid.NewGuid());
        var confirmation = await ReadSubsequentAsync(success);
        Assert.False(Assert.Single(confirmation.Incorporation.Items)
            .UnavailableProductExceptionApplied);
    }

    [Fact]
    public async Task Subsequent_mixed_ordinary_unavailable_line_rolls_back_without_consuming_pending()
    {
        await fixture.ResetAsync(Token);
        var initial = await fixture.CreateProductAsync("Initial", "3", Token);
        var unavailable = await fixture.CreateProductAsync("Unavailable", "17", Token);
        await fixture.SetProductPreparationAsync(unavailable.Id, Guid.NewGuid(), Token);
        var order = await CreateOrderAsync(initial.Id);
        await fixture.SetProductStateAsync(unavailable.Id, true, false, Token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var client = await fixture.LoginAsync(actor, Token);
        var pending = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, Token, client);
        var before = await fixture.CountSubsequentEffectsAsync(Token);
        var request = new SubsequentConfirmationRequest(pending.PendingCompositionId,
        [
            new(unavailable.Id, 1, "A", true),
            new(unavailable.Id, 1, "B")
        ]);

        using var response = await PostSubsequentAsync(
            client, order.OperationalReference, request, Guid.NewGuid());
        await AssertProblemAsync(response, HttpStatusCode.Conflict,
            "order_operations.first_confirmation.product_unavailable");
        Assert.Equal(before, await fixture.CountSubsequentEffectsAsync(Token));
        await using var scope = fixture.Services.CreateAsyncScope();
        var persisted = Assert.Single(await scope.ServiceProvider
            .GetRequiredService<OrderOperationsDbContext>().PendingCompositions
            .AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(pending.PendingCompositionId, persisted.Id);
    }

    [Fact]
    public async Task Subsequent_exact_replay_survives_both_revocations_and_flag_change_conflicts()
    {
        await fixture.ResetAsync(Token);
        var product = await fixture.CreateProductAsync("Product", "9", Token);
        var order = await CreateOrderAsync(product.Id);
        await fixture.SetProductStateAsync(product.Id, true, false, Token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var client = await fixture.LoginAsync(actor, Token);
        var pending = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, Token, client);
        var request = new SubsequentConfirmationRequest(pending.PendingCompositionId,
            [new(product.Id, 2, null, true)]);
        var key = Guid.NewGuid();

        using var original = await PostSubsequentAsync(
            client, order.OperationalReference, request, key);
        var originalBody = await original.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        var freshPending = await fixture.StartPendingCompositionAsync(
            order.OperationalReference, Token, client);
        var freshRequest = new SubsequentConfirmationRequest(
            freshPending.PendingCompositionId,
            [new(product.Id, 1, null, true)]);
        var before = await fixture.CountSubsequentEffectsAsync(Token);
        await fixture.RevokeOperationalInterventionAssignmentAsync(actor.IdentityId, Token);

        using (var replay = await PostSubsequentAsync(
            client, order.OperationalReference, request, key))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync(Token));
        }
        Assert.Equal(before, await fixture.CountSubsequentEffectsAsync(Token));

        using (var freshWithoutIntervention = await PostSubsequentAsync(
            client, order.OperationalReference, freshRequest, Guid.NewGuid()))
            await AssertProblemAsync(freshWithoutIntervention, HttpStatusCode.Forbidden,
                "order_operations.confirmation.operational_intervention_required");

        await fixture.RevokeOrderOperationsAssignmentAsync(actor.IdentityId, Token);
        using (var replayWithoutEitherResponsibility = await PostSubsequentAsync(
            client, order.OperationalReference, request, key))
        {
            Assert.Equal(HttpStatusCode.Created, replayWithoutEitherResponsibility.StatusCode);
            Assert.Equal(
                originalBody,
                await replayWithoutEitherResponsibility.Content.ReadAsStringAsync(Token));
        }
        Assert.Equal(before, await fixture.CountSubsequentEffectsAsync(Token));

        using (var freshWithoutBase = await PostSubsequentAsync(
            client, order.OperationalReference, freshRequest, Guid.NewGuid()))
            await AssertProblemAsync(freshWithoutBase, HttpStatusCode.Forbidden,
                "order_operations.confirmation.forbidden");

        var ordinary = request with
        {
            Items = [new SubsequentConfirmationItemRequest(product.Id, 2)]
        };
        using var conflict = await PostSubsequentAsync(
            client, order.OperationalReference, ordinary, key);
        await AssertProblemAsync(conflict, HttpStatusCode.Conflict,
            "order_operations.subsequent_confirmation.idempotency_key_conflict");
    }

    [Fact]
    public async Task Available_before_authoritative_exception_snapshot_succeeds_without_applied_marker()
    {
        await fixture.ResetAsync(Token);
        var product = await fixture.CreateProductAsync("Race", "13", Token);
        await fixture.SetProductStateAsync(product.Id, true, false, Token);
        var request = FirstRequest(product.Id, exceptional: true);
        await fixture.SetProductStateAsync(product.Id, true, true, Token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var client = await fixture.LoginAsync(actor, Token);

        using var response = await PostFirstAsync(client, request, Guid.NewGuid());
        var confirmation = await ReadFirstAsync(response);
        Assert.False(Assert.Single(confirmation.FirstIncorporation.Items)
            .UnavailableProductExceptionApplied);
    }

    [Fact]
    public async Task Available_when_ordinary_intent_is_built_but_unavailable_at_snapshot_rejects()
    {
        await fixture.ResetAsync(Token);
        var product = await fixture.CreateProductAsync("Race ordinary", "14", Token);
        var request = FirstRequest(product.Id, exceptional: false);
        await fixture.SetProductStateAsync(product.Id, true, false, Token);
        var actor = await fixture.CreateConfirmationActorAsync(true, true, Token);
        using var client = await fixture.LoginAsync(actor, Token);

        using var response = await PostFirstAsync(client, request, Guid.NewGuid());
        await AssertProblemAsync(response, HttpStatusCode.Conflict,
            "order_operations.first_confirmation.product_unavailable");
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(Token));
    }

    private async Task<FirstConfirmationResponse> CreateOrderAsync(Guid productId)
    {
        using var response = await PostFirstAsync(
            fixture.OrderOperationsClient,
            FirstRequest(productId, exceptional: false),
            Guid.NewGuid());
        return await ReadFirstAsync(response);
    }

    private static FirstConfirmationRequest FirstRequest(Guid productId, bool exceptional) =>
        new("Mesa S10", [new(productId, 1, null, exceptional)]);

    private static async Task<HttpResponseMessage> PostFirstAsync(
        HttpClient client,
        object request,
        Guid idempotencyKey)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post, "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(request, request.GetType())
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, message, Token);
    }

    private static async Task<HttpResponseMessage> PostSubsequentAsync(
        HttpClient client,
        string operationalReference,
        SubsequentConfirmationRequest request,
        Guid idempotencyKey)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{operationalReference}/confirmations")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, message, Token);
    }

    private static async Task<FirstConfirmationResponse> ReadFirstAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(Token));
    }

    private static async Task<SubsequentConfirmationResponse> ReadSubsequentAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<SubsequentConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<SubsequentConfirmationResponse>(Token));
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(Token), cancellationToken: Token);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
