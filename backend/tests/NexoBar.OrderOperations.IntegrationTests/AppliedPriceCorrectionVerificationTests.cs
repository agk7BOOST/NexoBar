using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed partial class AppliedPriceCorrectionApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<string> SnapshotAsync(string table)
    {
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, Token);
        await using var command = connection.CreateCommand();
        var name = new Npgsql.NpgsqlCommandBuilder().QuoteIdentifier(table);
        command.CommandText = $"SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text), '[]'::jsonb)::text FROM order_operations.{name} r";
        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, DeliveryTarget target, Guid key, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/order-operations/orders/{target.OperationalReference}/incorporations/{target.IncorporationId}/contents/{target.ContentOrdinal}/apply-current-catalog-price")
        { Content = JsonContent.Create(body ?? new ApplyCurrentCatalogPriceRequest()) };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);
    }

    private async Task<DeliveryTarget> CreateTenAsync()
    {
        await fixture.ResetAsync(Token);
        var result = await LiquidationTestSupport.CreateDirectOrderAsync(fixture, [("10", 2)], Token);
        var content = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token));
        return new(result.OperationalReference, content.IncorporationId, content.ContentOrdinal, null);
    }

    private async Task<AppliedPriceCorrectionResponse> CorrectToAsync(DeliveryTarget target, decimal value)
    {
        var content = (await fixture.ReadConfirmedContentsAsync(Token)).Single(x => x.IncorporationId == target.IncorporationId && x.ContentOrdinal == target.ContentOrdinal);
        await SetCatalogPriceAsync(content.ProductId, value, Token);
        using var response = await PostAsync(target, Guid.NewGuid(), Token);
        return await ReadSuccessAsync(response, Token);
    }

    private async Task AssertAmountAsync(DeliveryTarget target, string expected)
    {
        var read = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, Token);
        Assert.Equal(expected, read.FunctionalAmount);
    }

    [Theory]
    [InlineData("CatalogConfiguration")]
    [InlineData("OperationalIntervention")]
    public async Task Other_responsibility_alone_cannot_apply_price(string responsibility)
    {
        var target = await CreateTenAsync();
        var actor = await fixture.CreateDeliveryActorAsync(false, false, null, Token);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var identities = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
            identities.ResponsibilityAssignments.Add(new(actor.IdentityId, Enum.Parse<FunctionalResponsibility>(responsibility)));
            await identities.SaveChangesAsync(Token);
        }
        using var client = await fixture.LoginAsync(actor, Token);
        var product = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token)).ProductId;
        await SetCatalogPriceAsync(product, 8m, Token);
        using var denied = await SendAsync(client, target, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await AssertPriceStateAsync(target.IncorporationId, target.ContentOrdinal, 10m, Token);
        // The default actor has only OrderOperationsAndBasicClosure, without Preparation or enablement.
        using var allowed = await PostAsync(target, Guid.NewGuid(), Token);
        await ReadSuccessAsync(allowed, Token);
    }

    [Fact]
    public async Task Replay_after_catalog_change_and_revocation_bypasses_catalog_then_new_intent_adopts_new_price()
    {
        var target = await CreateTenAsync();
        var product = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token)).ProductId;
        var key = Guid.NewGuid();
        await SetCatalogPriceAsync(product, 8m, Token);
        using var first = await PostAsync(target, key, Token);
        var original = await ReadSuccessAsync(first, Token);
        await SetCatalogPriceAsync(product, 9m, Token);
        await fixture.RevokeOrderOperationsAssignmentAsync(fixture.DefaultOrderOperationsActor.IdentityId, Token);
        // A Catalog read would block behind this lock. Replay must complete while it is held.
        await using (var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, Token))
        await using (var transaction = await connection.BeginTransactionAsync(Token))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM catalog.products WHERE id = @id FOR UPDATE";
            command.Parameters.AddWithValue("id", product);
            await command.ExecuteScalarAsync(Token);
            using var replay = await PostAsync(target, key, Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(original, await ReadSuccessAsync(replay, Token));
            await transaction.RollbackAsync(Token);
        }
        await AssertPriceStateAsync(target.IncorporationId, target.ContentOrdinal, 8m, Token);
        using var denied = await PostAsync(target, Guid.NewGuid(), Token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var identities = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
            identities.ResponsibilityAssignments.Add(new(fixture.DefaultOrderOperationsActor.IdentityId, FunctionalResponsibility.OrderOperationsAndBasicClosure));
            await identities.SaveChangesAsync(Token);
        }
        using var next = await PostAsync(target, Guid.NewGuid(), Token);
        Assert.Equal("9", (await ReadSuccessAsync(next, Token)).ResultingEffectiveAppliedPrice);
        await using var finalScope = fixture.Services.CreateAsyncScope();
        var db = finalScope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Equal(2, await db.AppliedPriceCorrectionHistory.CountAsync(Token));
        Assert.Equal(2, await db.AppliedPriceCorrectionCommands.CountAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Committed_key_rejects_different_target_or_actor(bool differentActor)
    {
        var target = await CreateTenAsync();
        var product = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token)).ProductId;
        await SetCatalogPriceAsync(product, 8m, Token);
        var key = Guid.NewGuid();
        using var first = await PostAsync(target, key, Token);
        await ReadSuccessAsync(first, Token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, Token);
        using var otherClient = await fixture.LoginAsync(actor, Token);
        using var conflict = await SendAsync(differentActor ? otherClient : fixture.OrderOperationsClient,
            differentActor ? target : target with { ContentOrdinal = 999 }, key);
        await DeliveryQuantityTestSupport.AssertProblemAsync(conflict, HttpStatusCode.Conflict,
            "order_operations.applied_price_correction.idempotency_key_conflict", Token);
        await using var scope = fixture.Services.CreateAsyncScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>().AppliedPriceCorrectionHistory.ToArrayAsync(Token));
    }

    [Fact]
    public async Task Successive_corrections_keep_history_even_when_returning_to_original_and_final_noop_is_empty()
    {
        var target = await CreateTenAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var confirmationBefore = await SnapshotAsync("confirmation_history");
        var results = new List<AppliedPriceCorrectionResponse>();
        foreach (var price in new[] { 8m, 9m, 10m }) results.Add(await CorrectToAsync(target, price));
        using var noop = await PostAsync(target, Guid.NewGuid(), Token);
        Assert.Equal(HttpStatusCode.Conflict, noop.StatusCode);
        var history = await db.AppliedPriceCorrectionHistory.AsNoTracking().OrderBy(x => x.OccurredAt).ToArrayAsync(Token);
        Assert.Equal(new[] { (10m, 8m), (8m, 9m), (9m, 10m) }, history.Select(x => (x.PreviousEffectiveAppliedPrice, x.ResultingEffectiveAppliedPrice)));
        Assert.All(history, x =>
        {
            Assert.Equal(target.IncorporationId, x.IncorporationId);
            Assert.Equal(target.ContentOrdinal, x.ContentOrdinal);
            Assert.Equal(fixture.DefaultOrderOperationsActor.IdentityId, x.ActorIdentityId);
            Assert.Equal(TimeSpan.Zero, x.OccurredAt.Offset);
            Assert.Contains(results, result => result.HistoryId == x.Id && result.OccurredAt == x.OccurredAt);
        });
        Assert.Equal(3, await db.AppliedPriceCorrectionCommands.CountAsync(Token));
        Assert.Equal(10m, (await db.IncorporationContents.SingleAsync(Token)).AppliedPrice);
        Assert.Equal(confirmationBefore, await SnapshotAsync("confirmation_history"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_cancellation_or_liquidation_rejects_new_price_intent(bool liquidate)
    {
        var target = await CreateTenAsync();
        if (liquidate)
        {
            await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 2, Token);
            using var response = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), Token);
            await LiquidationTestSupport.ReadSuccessAsync(response, Token);
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{target.OperationalReference}/complete-cancellation");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, Token);
            response.EnsureSuccessStatusCode();
        }
        var product = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token)).ProductId;
        await SetCatalogPriceAsync(product, 8m, Token);
        using var rejected = await PostAsync(target, Guid.NewGuid(), Token);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        await AssertPriceStateAsync(target.IncorporationId, target.ContentOrdinal, 10m, Token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Empty(await db.AppliedPriceCorrectionHistory.ToArrayAsync(Token));
        Assert.Empty(await db.AppliedPriceCorrectionCommands.ToArrayAsync(Token));
    }

    [Fact]
    public async Task Open_zero_fulfillment_and_pending_composition_allow_correction_without_quantity_changes()
    {
        var target = await CreateTenAsync();
        using var cancel = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 2, Token);
        cancel.EnsureSuccessStatusCode();
        await fixture.StartPendingCompositionAsync(target.OperationalReference, Token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var pendingBefore = await SnapshotAsync("pending_compositions");
        var quantityBefore = await SnapshotAsync("content_quantity_states");
        var deliveryBefore = await SnapshotAsync("delivery_states");
        await AssertAmountAsync(target, "0");
        await CorrectToAsync(target, 8m);
        await AssertAmountAsync(target, "0");
        Assert.Equal(pendingBefore, await SnapshotAsync("pending_compositions"));
        Assert.Equal(quantityBefore, await SnapshotAsync("content_quantity_states"));
        Assert.Equal(deliveryBefore, await SnapshotAsync("delivery_states"));
        Assert.Empty(await db.OrderCancellationStates.ToArrayAsync(Token));
    }

    [Fact]
    public async Task Delivery_correction_and_liquidation_use_adopted_price_despite_later_catalog_change()
    {
        var target = await CreateTenAsync();
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 2, Token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var deliveryBefore = await SnapshotAsync("delivery_states");
        var historyBefore = await SnapshotAsync("delivery_history");
        await CorrectToAsync(target, 8m);
        await AssertAmountAsync(target, "16");
        Assert.Equal(deliveryBefore, await SnapshotAsync("delivery_states"));
        Assert.Equal(historyBefore, await SnapshotAsync("delivery_history"));
        var product = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token)).ProductId;
        await SetCatalogPriceAsync(product, 9m, Token);
        using var correction = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, Token);
        await DeliveryCorrectionTestSupport.SuccessAsync(correction, Token);
        await AssertAmountAsync(target, "8");
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 1, Token);
        using var liquidation = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), Token);
        Assert.Equal("16", (await LiquidationTestSupport.ReadSuccessAsync(liquidation, Token)).FunctionalAmount);
        await AssertPriceStateAsync(target.IncorporationId, target.ContentOrdinal, 8m, Token);
    }

    [Fact]
    public async Task Exact_content_isolation_and_confirmation_prices_survive_correction()
    {
        var target = await CreateTenAsync();
        var product = Assert.Single(await fixture.ReadConfirmedContentsAsync(Token)).ProductId;
        var pending = await fixture.StartPendingCompositionAsync(target.OperationalReference, Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/order-operations/orders/{target.OperationalReference}/confirmations")
        { Content = JsonContent.Create(new SubsequentConfirmationRequest(pending.PendingCompositionId,
            [new SubsequentConfirmationItemRequest(product, 1)])) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var confirmed = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, Token);
        confirmed.EnsureSuccessStatusCode();
        using var otherRequest = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
        { Content = JsonContent.Create(new FirstConfirmationRequest("Other order", [new FirstConfirmationItemRequest(product, 1)])) };
        otherRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var other = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, otherRequest, Token);
        other.EnsureSuccessStatusCode();
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Equal(3, await db.ContentAppliedPriceStates.CountAsync(Token));
        Assert.All(await db.ContentAppliedPriceStates.AsNoTracking().ToArrayAsync(Token), x => Assert.Equal(10m, x.EffectiveAppliedPrice));
        await CorrectToAsync(target, 8m);
        Assert.All(await db.ContentAppliedPriceStates.AsNoTracking().ToArrayAsync(Token), x =>
            Assert.Equal(x.IncorporationId == target.IncorporationId ? 8m : 10m, x.EffectiveAppliedPrice));
        Assert.All(await db.IncorporationContents.AsNoTracking().ToArrayAsync(Token), x => Assert.Equal(10m, x.AppliedPrice));
    }

    [Fact]
    public async Task Missing_mandatory_price_state_rejects_instead_of_using_confirmation_price()
    {
        var target = await CreateTenAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        await db.ContentAppliedPriceStates.ExecuteDeleteAsync(Token);
        using var response = await PostAsync(target, Guid.NewGuid(), Token);
        await DeliveryQuantityTestSupport.AssertProblemAsync(response, HttpStatusCode.InternalServerError,
            "order_operations.applied_price_correction.state_inconsistent", Token);
        Assert.Empty(await db.AppliedPriceCorrectionHistory.ToArrayAsync(Token));
    }

    [Fact]
    public async Task Client_cannot_supply_an_arbitrary_price()
    {
        var target = await CreateTenAsync();
        using var response = await SendAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), new { newPrice = "1" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertPriceStateAsync(target.IncorporationId, target.ContentOrdinal, 10m, Token);
    }
}
