using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed partial class OperationalInterventionTests(OrderOperationsApiFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(DeliveryTarget Target, PreparationActor Actor, HttpClient Client)> Setup(int started = 5, int ready = 3, int delivered = 1, int total = 7)
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, total, 0, Token);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        using var preparer = await fixture.LoginAsync(await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, Token), Token);
        if (started > 0)
        {
            using var start = await PreparationStartTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), started, Token);
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        }
        if (ready > 0)
        {
            using var response = await PreparationReadyTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), ready, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        if (delivered > 0) await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, delivered, Token);
        var actor = await CreateActor();
        return (target, actor, await fixture.LoginAsync(actor, Token));
    }

    private async Task<PreparationActor> CreateActor()
    {
        var actor = await fixture.CreatePreparationActorAsync(false, null, Token);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        db.ResponsibilityAssignments.Add(new ResponsibilityAssignment(actor.IdentityId, FunctionalResponsibility.OperationalIntervention));
        await db.SaveChangesAsync(Token);
        return actor;
    }

    private static string Path(DeliveryTarget target, bool ready) =>
        $"/api/order-operations/intervention/work/{target.WorkId}/{(ready ? "ready" : "in-preparation")}";
    private static string ReadPath(DeliveryTarget target) =>
        $"/api/order-operations/intervention/incorporations/{target.IncorporationId}/contents/{target.ContentOrdinal}";

    private static async Task<HttpResponseMessage> Intervene(HttpClient client, DeliveryTarget target, bool ready, int quantity = 1, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(target, ready)) { Content = JsonContent.Create(new { quantity }) };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);
    }

    private static async Task<OperationalInterventionResponse> Success(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<OperationalInterventionResponse>(await response.Content.ReadFromJsonAsync<OperationalInterventionResponse>(Token));
    }

    private async Task AssertInvariants()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var contents = await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>().IncorporationContents.AsNoTracking().ToArrayAsync(Token);
        var quantities = await fixture.ReadContentQuantityStatesAsync(Token);
        var deliveries = await fixture.ReadDeliveryStatesAsync(Token);
        foreach (var work in await fixture.ReadPreparationWorkAsync(Token))
        {
            var content = contents.Single(x => x.IncorporationId == work.IncorporationId && x.ContentOrdinal == work.ContentOrdinal);
            var state = quantities.Single(x => x.IncorporationId == work.IncorporationId && x.ContentOrdinal == work.ContentOrdinal);
            var delivered = deliveries.Single(x => x.IncorporationId == work.IncorporationId && x.ContentOrdinal == work.ContentOrdinal).DeliveredQuantity;
            Assert.Equal(content.Quantity - state.RemovedByCorrectionQuantity - state.CancelledQuantity, work.TotalQuantity);
            Assert.Equal(work.TotalQuantity, work.PendingQuantity + work.InPreparationQuantity + work.ReadyQuantity);
            Assert.InRange(work.PendingQuantity, 0, work.TotalQuantity);
            Assert.InRange(work.InPreparationQuantity, 0, work.TotalQuantity);
            Assert.InRange(work.ReadyQuantity, 0, work.TotalQuantity);
            Assert.InRange(delivered, 0, work.ReadyQuantity);
        }
    }

    [Theory]
    [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 1)] [InlineData(true, 2)]
    public async Task Exact_partial_and_source_boundaries_preserve_real_history_and_economics(bool ready, int quantity)
    {
        var s = await Setup(); using var client = s.Client;
        var contents = await fixture.ReadConfirmedContentsAsync(Token);
        await using var contentScope = fixture.Services.CreateAsyncScope();
        var contentDb = contentScope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var contentBefore = await contentDb.IncorporationContents.AsNoTracking().Select(c => new
        { c.IncorporationId, c.ContentOrdinal, c.ProductId, c.Quantity, c.Instruction, c.AppliedPrice, c.RequiresPreparationAtConfirmation }).SingleAsync(Token);
        var destination = Assert.Single(await fixture.ReadPreparationWorkAsync(Token)).PreparationResponsibilityId;
        var originalHistory = HistorySnapshot(await fixture.ReadPreparationHistoryAsync(Token));
        var histories = await fixture.ReadPreparationHistoryAsync(Token);
        var commands = await fixture.ReadPreparationCommandsAsync(Token);
        var deliveries = await fixture.ReadDeliveryStatesAsync(Token);
        var economics = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, s.Target.OperationalReference, Token);
        using var response = await Intervene(client, s.Target, ready, quantity);
        var result = await Success(response);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        Assert.Equal((7 - quantity, 2, ready ? 2 : 2 - quantity, ready ? 3 - quantity : 3),
            (work.TotalQuantity, work.PendingQuantity, work.InPreparationQuantity, work.ReadyQuantity));
        var state = Assert.Single(await fixture.ReadContentQuantityStatesAsync(Token));
        Assert.Equal((0, quantity), (state.RemovedByCorrectionQuantity, state.CancelledQuantity));
        Assert.Equal(contents, await fixture.ReadConfirmedContentsAsync(Token));
        Assert.Equal(contentBefore, await contentDb.IncorporationContents.AsNoTracking().Select(c => new
        { c.IncorporationId, c.ContentOrdinal, c.ProductId, c.Quantity, c.Instruction, c.AppliedPrice, c.RequiresPreparationAtConfirmation }).SingleAsync(Token));
        Assert.Equal(destination, work.PreparationResponsibilityId);
        Assert.Equal(deliveries, await fixture.ReadDeliveryStatesAsync(Token));
        var after = await fixture.ReadPreparationHistoryAsync(Token);
        Assert.Equal(originalHistory, HistorySnapshot(after.Where(h => histories.Any(x => x.Id == h.Id))));
        var intervention = Assert.Single(after, h => h.Id == result.HistoryId);
        Assert.Equal(ready ? PreparationHistory.ReadyIntervenedEventKind : PreparationHistory.InPreparationIntervenedEventKind, intervention.EventKind);
        Assert.Equal((work.Id, quantity, s.Actor.IdentityId, result.OccurredAt),
            (intervention.WorkId, intervention.Quantity, intervention.ActorIdentityId, intervention.OccurredAt));
        Assert.Equal(TimeSpan.Zero, intervention.OccurredAt.Offset);
        Assert.Equal((result.TotalQuantity, result.PendingQuantity, result.InPreparationQuantity, result.ReadyQuantity),
            (intervention.ResultingTotalQuantity, intervention.ResultingPendingQuantity, intervention.ResultingInPreparationQuantity, intervention.ResultingReadyQuantity));
        Assert.Equal(commands.Count + 1, (await fixture.ReadPreparationCommandsAsync(Token)).Count);
        var currentEconomics = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, s.Target.OperationalReference, Token);
        Assert.Equal(economics.FunctionalAmount, currentEconomics.FunctionalAmount);
        Assert.False(currentEconomics.IsFrozen);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Empty(await db.ContentCancellationHistory.ToArrayAsync(Token));
        await AssertInvariants();
    }

    private static string HistorySnapshot(IEnumerable<PreparationHistory> histories) => JsonSerializer.Serialize(histories.Select(h => new
    {
        h.Id, h.WorkId, h.EventKind, h.Quantity, h.ActorIdentityId, h.OccurredAt,
        h.ResultingTotalQuantity, h.ResultingPendingQuantity, h.ResultingInPreparationQuantity, h.ResultingReadyQuantity
    }));
    [Theory]
    [InlineData(false, 0)] [InlineData(false, -1)] [InlineData(false, 3)] [InlineData(false, int.MaxValue)]
    [InlineData(true, 0)] [InlineData(true, -1)] [InlineData(true, 3)] [InlineData(true, int.MaxValue)]
    public async Task Invalid_or_excess_quantity_never_clips_or_consumes_delivered_ready(bool ready, int quantity)
    {
        var s = await Setup(); using var client = s.Client;
        var before = await fixture.ReadPreparationWorkAsync(Token);
        using var response = await Intervene(client, s.Target, ready, quantity);
        Assert.Equal(quantity <= 0 ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await fixture.ReadPreparationWorkAsync(Token));
        Assert.Equal(0, Assert.Single(await fixture.ReadContentQuantityStatesAsync(Token)).CancelledQuantity);
        Assert.Equal(2, (await fixture.ReadPreparationHistoryAsync(Token)).Count);
        Assert.Equal(2, (await fixture.ReadPreparationCommandsAsync(Token)).Count);
        await AssertInvariants();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Zero_obligation_preserves_pending_composition_and_does_not_liquidate(bool ready)
    {
        var s = await Setup(3, ready ? 3 : 0, 0, 3); using var client = s.Client;
        var marker = await fixture.StartPendingCompositionAsync(s.Target.OperationalReference, Token);
        using var response = await Intervene(client, s.Target, ready, 3);
        Assert.Equal(0, (await Success(response)).TotalQuantity);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var pending = await db.PendingCompositions.SingleAsync(Token);
        Assert.Equal((marker.PendingCompositionId, marker.CreatedAt, marker.CreatedByIdentityId), (pending.Id, pending.CreatedAt, pending.CreatedByIdentityId));
        Assert.Equal(Guid.Parse(s.Target.OperationalReference), pending.OrderId);
        Assert.Empty(await db.Liquidations.ToArrayAsync(Token));
        Assert.Empty(await db.Closures.ToArrayAsync(Token));
        var order = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, s.Target.OperationalReference, Token);
        Assert.Equal("0", order.FunctionalAmount);
        Assert.Equal(["pending_composition"], order.LiquidationBlockers);
        using var read = await client.GetAsync(ReadPath(s.Target), Token);
        var target = await read.Content.ReadFromJsonAsync<OperationalInterventionTargetResponse>(Token);
        Assert.Equal(0, target!.FulfillmentQuantity);
        using var again = await Intervene(client, s.Target, ready);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        await AssertInvariants();
    }
}
