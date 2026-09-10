using System.Net;
using System.Net.Http.Json;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed partial class OperationalInterventionTests
{
    [Theory]
    [InlineData("start", false, false)] [InlineData("start", false, true)]
    [InlineData("mark-ready", false, false)] [InlineData("mark-ready", false, true)]
    [InlineData("correct-start", false, false)] [InlineData("correct-start", false, true)]
    [InlineData("correct-ready", true, false)] [InlineData("correct-ready", true, true)]
    [InlineData("cancel-content", false, false)] [InlineData("cancel-content", false, true)]
    [InlineData("cancel-content", true, false)] [InlineData("cancel-content", true, true)]
    [InlineData("correct-content", false, false)] [InlineData("correct-content", false, true)]
    [InlineData("correct-content", true, false)] [InlineData("correct-content", true, true)]
    [InlineData("delivery", true, false)] [InlineData("delivery", true, true)]
    [InlineData("correct-delivery", true, false)] [InlineData("correct-delivery", true, true)]
    public async Task Races_revalidate_exact_source_under_same_order_lock(string competing, bool ready, bool interventionFirst)
    {
        var s = await Setup(delivered: competing == "correct-delivery" ? 3 : 1); using var client = s.Client;
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        using var preparer = await fixture.LoginAsync(await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, Token), Token);
        Task<HttpResponseMessage> Intervention() => Intervene(client, s.Target, ready, 2);
        async Task<HttpResponseMessage> PreparationCorrection()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/order-operations/preparation/work/{work.Id}/{competing}")
            { Content = JsonContent.Create(new { quantity = 2 }) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(preparer, request, Token);
        }
        Task<HttpResponseMessage> Compete() => competing switch
        {
            "start" => PreparationStartTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), 2, Token),
            "mark-ready" => PreparationReadyTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), 2, Token),
            "correct-start" or "correct-ready" => PreparationCorrection(),
            "cancel-content" => ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, s.Target, Guid.NewGuid(), 2, Token),
            "correct-content" => ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, s.Target, Guid.NewGuid(), 2, Token),
            "delivery" => DeliveryQuantityTestSupport.PostAsync(fixture.OrderOperationsClient, s.Target.IncorporationId, s.Target.ContentOrdinal, Guid.NewGuid(), 2, Token),
            "correct-delivery" => DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, s.Target, Guid.NewGuid(), 2, Token),
            _ => throw new InvalidOperationException()
        };
        var responses = await Race(s.Target, interventionFirst, Intervention, Compete);
        using var intervention = responses.Intervention; using var competitor = responses.Competitor;
        var sharedSource = competing is "mark-ready" or "correct-start" or "correct-ready" or "delivery";
        var interventionSucceeded = competing == "correct-delivery" ? !interventionFirst : !sharedSource || interventionFirst;
        Assert.Equal(interventionSucceeded ? HttpStatusCode.OK : HttpStatusCode.Conflict, intervention.StatusCode);
        Assert.Equal(sharedSource && interventionFirst ? HttpStatusCode.Conflict : HttpStatusCode.OK, competitor.StatusCode);
        var after = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        var quantities = Assert.Single(await fixture.ReadContentQuantityStatesAsync(Token));
        var expectedC = (interventionSucceeded ? 2 : 0) + (competing == "cancel-content" ? 2 : 0);
        var expectedR = competing == "correct-content" ? 2 : 0;
        Assert.Equal((expectedR, expectedC), (quantities.RemovedByCorrectionQuantity, quantities.CancelledQuantity));
        Assert.Equal(7 - expectedC - expectedR, after.TotalQuantity);
        var expected = competing switch
        {
            "start" => (0, 2, 3),
            "mark-ready" => interventionFirst ? (2, 0, 3) : (2, 0, 5),
            "correct-start" => interventionFirst ? (2, 0, 3) : (4, 0, 3),
            "correct-ready" => interventionFirst ? (2, 2, 1) : (2, 4, 1),
            "cancel-content" or "correct-content" => ready ? (0, 2, 1) : (0, 0, 3),
            "delivery" => interventionFirst ? (2, 2, 1) : (2, 2, 3),
            _ => interventionFirst ? (2, 2, 3) : (2, 2, 1)
        };
        Assert.Equal(expected, (after.PendingQuantity, after.InPreparationQuantity, after.ReadyQuantity));
        Assert.Equal(competing == "delivery" && !interventionFirst ? 3 : 1, Assert.Single(await fixture.ReadDeliveryStatesAsync(Token)).DeliveredQuantity);
        var history = await fixture.ReadPreparationHistoryAsync(Token);
        Assert.Equal(interventionSucceeded ? 1 : 0, history.Count(x => x.EventKind is PreparationHistory.InPreparationIntervenedEventKind or PreparationHistory.ReadyIntervenedEventKind));
        await AssertInvariants();
    }

    private async Task<(HttpResponseMessage Intervention, HttpResponseMessage Competitor)> Race(
        DeliveryTarget target, bool interventionFirst, Func<Task<HttpResponseMessage>> intervention, Func<Task<HttpResponseMessage>> competitor)
    {
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, Token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, Token);
        var first = interventionFirst ? intervention() : competitor();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), Token));
        var second = interventionFirst ? competitor() : intervention();
        Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(2, TimeSpan.FromSeconds(10), Token));
        await blocker.CommitAsync(Token);
        var a = await first; var b = await second;
        return interventionFirst ? (a, b) : (b, a);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Liquidation_revalidates_reduced_obligation_and_freeze_rejects_new_intervention(bool ready, bool interventionFirst)
    {
        var s = await Setup(3, ready ? 3 : 2, 2, 3); using var client = s.Client;
        var key = Guid.NewGuid();
        var responses = await Race(s.Target, interventionFirst,
            () => Intervene(client, s.Target, ready, 1, key),
            () => LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, s.Target.OperationalReference, Guid.NewGuid(), Token));
        using var intervention = responses.Intervention; using var liquidation = responses.Competitor;
        await Success(intervention);
        Assert.Equal(interventionFirst ? HttpStatusCode.OK : HttpStatusCode.Conflict, liquidation.StatusCode);
        var read = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, s.Target.OperationalReference, Token);
        Assert.Equal("14", read.FunctionalAmount);
        Assert.Equal(interventionFirst, read.IsFrozen);
        if (!interventionFirst)
        {
            using var liquidate = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, s.Target.OperationalReference, Guid.NewGuid(), Token);
            Assert.Equal(HttpStatusCode.OK, liquidate.StatusCode);
        }
        using var frozen = await Intervene(client, s.Target, ready);
        await DeliveryQuantityTestSupport.AssertProblemAsync(frozen, HttpStatusCode.Conflict, "order_operations.order.frozen", Token);
        using var targetRead = await client.GetAsync(ReadPath(s.Target), Token);
        var target = (await targetRead.Content.ReadFromJsonAsync<OperationalInterventionTargetResponse>(Token))!;
        Assert.True(target.IsFrozen);
        Assert.Equal((0, 0), (target.IntervenableInPreparationQuantity, target.IntervenableReadyQuantity));
        using var replay = await Intervene(client, s.Target, ready, 1, key);
        Assert.Equal(await intervention.Content.ReadAsStringAsync(Token), await replay.Content.ReadAsStringAsync(Token));
        await AssertInvariants();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Liquidation_wins_then_waiting_intervention_observes_freeze(bool ready)
    {
        var s = await Setup(3, 3, 3, 3); using var client = s.Client;
        var responses = await Race(s.Target, false, () => Intervene(client, s.Target, ready),
            () => LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, s.Target.OperationalReference, Guid.NewGuid(), Token));
        using var intervention = responses.Intervention; using var liquidation = responses.Competitor;
        Assert.Equal(HttpStatusCode.OK, liquidation.StatusCode);
        await DeliveryQuantityTestSupport.AssertProblemAsync(intervention, HttpStatusCode.Conflict, "order_operations.order.frozen", Token);
        Assert.Equal(0, Assert.Single(await fixture.ReadContentQuantityStatesAsync(Token)).CancelledQuantity);
        await AssertInvariants();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Concurrent_identical_key_has_one_durable_effect(bool ready)
    {
        var s = await Setup(); using var client = s.Client; var key = Guid.NewGuid();
        var responses = await Task.WhenAll(Intervene(client, s.Target, ready, 2, key), Intervene(client, s.Target, ready, 2, key));
        using var a = responses[0]; using var b = responses[1];
        Assert.Equal(await Success(a), await Success(b));
        Assert.Equal(3, (await fixture.ReadPreparationHistoryAsync(Token)).Count);
        Assert.Equal(3, (await fixture.ReadPreparationCommandsAsync(Token)).Count);
        Assert.Equal(2, Assert.Single(await fixture.ReadContentQuantityStatesAsync(Token)).CancelledQuantity);
        await AssertInvariants();
    }
}
