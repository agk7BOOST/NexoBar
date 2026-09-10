using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed partial class OperationalInterventionTests
{
    [Theory]
    [InlineData(false, true)] [InlineData(false, false)]
    [InlineData(true, true)] [InlineData(true, false)]
    public async Task Persistence_failure_rolls_back_C_Work_history_and_command(bool ready, bool historyFailure)
    {
        var s = await Setup(); using var client = s.Client;
        var work = await fixture.ReadPreparationWorkAsync(Token);
        var delivery = await fixture.ReadDeliveryStatesAsync(Token);
        var key = Guid.NewGuid();
        if (historyFailure) await fixture.SetPreparationHistoryFailureAsync(true, Token);
        else await fixture.SetPreparationCommandFailureAsync(true, Token);
        try
        {
            using var failed = await Intervene(client, s.Target, ready, key: key);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(work, await fixture.ReadPreparationWorkAsync(Token));
            Assert.Equal(delivery, await fixture.ReadDeliveryStatesAsync(Token));
            Assert.Equal(0, Assert.Single(await fixture.ReadContentQuantityStatesAsync(Token)).CancelledQuantity);
            Assert.Equal(2, (await fixture.ReadPreparationHistoryAsync(Token)).Count);
            Assert.Equal(2, (await fixture.ReadPreparationCommandsAsync(Token)).Count);
        }
        finally
        {
            if (historyFailure) await fixture.SetPreparationHistoryFailureAsync(false, Token);
            else await fixture.SetPreparationCommandFailureAsync(false, Token);
        }
        using var retry = await Intervene(client, s.Target, ready, key: key);
        await Success(retry);
        await AssertInvariants();
    }

    [Theory]
    [InlineData("delivery")] [InlineData("quantities")] [InlineData("work")]
    [InlineData("fulfillment")] [InlineData("delivered-boundary")] [InlineData("classification")]
    public async Task Missing_or_incoherent_required_state_is_never_a_zero_fallback(string corruption)
    {
        var s = await Setup(0, 0, 0); using var client = s.Client;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
            switch (corruption)
            {
                case "delivery": await db.DeliveryStates.ExecuteDeleteAsync(Token); break;
                case "quantities": await db.ContentQuantityStates.ExecuteDeleteAsync(Token); break;
                case "work": await db.PreparationWork.ExecuteDeleteAsync(Token); break;
                case "fulfillment": await db.ContentQuantityStates.ExecuteUpdateAsync(x => x.SetProperty(q => q.CancelledQuantity, 1), Token); break;
                case "delivered-boundary": await db.DeliveryStates.ExecuteUpdateAsync(x => x.SetProperty(d => d.DeliveredQuantity, 1), Token); break;
                case "classification": await db.IncorporationContents.ExecuteUpdateAsync(x => x.SetProperty(c => c.RequiresPreparationAtConfirmation, false), Token); break;
            }
        }
        using var read = await client.GetAsync(ReadPath(s.Target), Token);
        await DeliveryQuantityTestSupport.AssertProblemAsync(read, HttpStatusCode.InternalServerError, "order_operations.intervention.state_inconsistent", Token);
        foreach (var ready in new[] { false, true })
        {
            using var command = await Intervene(client, s.Target, ready);
            // A Work UUID with no row is an unknown command target; exact Content lookup detects the missing required Work.
            Assert.Equal(corruption == "work" ? HttpStatusCode.NotFound : HttpStatusCode.InternalServerError, command.StatusCode);
        }
        Assert.Empty(await fixture.ReadPreparationHistoryAsync(Token));
        Assert.Empty(await fixture.ReadPreparationCommandsAsync(Token));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Existing_R_and_ordinary_C_remain_separate_from_intervention_provenance(bool ready)
    {
        var s = await Setup(); using var client = s.Client;
        using var correction = await ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, s.Target, Guid.NewGuid(), 1, Token);
        Assert.Equal(HttpStatusCode.OK, correction.StatusCode);
        using var cancellation = await ContentCancellationTestSupport.PostAsync(fixture.OrderOperationsClient, s.Target, Guid.NewGuid(), 1, Token);
        Assert.Equal(HttpStatusCode.OK, cancellation.StatusCode);
        using var intervention = await Intervene(client, s.Target, ready, 2);
        Assert.Equal(3, (await Success(intervention)).TotalQuantity);
        var quantities = Assert.Single(await fixture.ReadContentQuantityStatesAsync(Token));
        Assert.Equal((1, 3), (quantities.RemovedByCorrectionQuantity, quantities.CancelledQuantity));
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        Assert.Single(await db.ContentCancellationHistory.ToArrayAsync(Token));
        Assert.Single(await db.ContentCorrectionHistory.ToArrayAsync(Token));
        await AssertInvariants();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Same_product_contents_and_incorporations_are_isolated_by_exact_identity(bool ready)
    {
        await fixture.ResetAsync(Token);
        var product = await fixture.CreateProductAsync("Igual", "7", Token);
        var destination = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(product.Id, destination, Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/order-operations/first-confirmations")
        { Content = JsonContent.Create(new FirstConfirmationRequest("Mesa", [new(product.Id, 3, "uno"), new(product.Id, 3, "dos")])) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var confirmed = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, Token);
        confirmed.EnsureSuccessStatusCode();
        var order = (await confirmed.Content.ReadFromJsonAsync<FirstConfirmationResponse>(Token))!;
        var pending = await fixture.StartPendingCompositionAsync(order.OperationalReference, Token);
        using var subsequentRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/order-operations/orders/{order.OperationalReference}/confirmations")
        { Content = JsonContent.Create(new SubsequentConfirmationRequest(pending.PendingCompositionId, [new(product.Id, 3, "uno")])) };
        subsequentRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var subsequent = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, subsequentRequest, Token);
        subsequent.EnsureSuccessStatusCode();
        var works = await fixture.ReadPreparationWorkAsync(Token);
        using var preparer = await fixture.LoginAsync(await fixture.CreatePreparationActorAsync(true, destination, Token), Token);
        foreach (var work in works)
        {
            using var start = await PreparationStartTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), 3, Token);
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            if (ready)
            {
                using var marked = await PreparationReadyTestSupport.PostAsync(preparer, work.Id, Guid.NewGuid(), 3, Token);
                Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
            }
        }
        var before = await fixture.ReadPreparationWorkAsync(Token);
        var selected = before.First(x => x.IncorporationId == order.FirstIncorporation.Id);
        var target = new DeliveryTarget(order.OperationalReference, selected.IncorporationId, selected.ContentOrdinal, selected.Id);
        using var client = await fixture.LoginAsync(await CreateActor(), Token);
        using var response = await Intervene(client, target, ready, 2);
        await Success(response);
        using var read = await client.GetAsync(ReadPath(target), Token);
        var result = (await read.Content.ReadFromJsonAsync<OperationalInterventionTargetResponse>(Token))!;
        Assert.Equal(selected.Id, result.WorkId);
        Assert.Equal(product.Id, result.ProductId);
        Assert.Equal("Igual", result.ProductOperationalName);
        await using var scope = fixture.Services.CreateAsyncScope();
        var content = await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>().IncorporationContents.AsNoTracking()
            .SingleAsync(x => x.IncorporationId == selected.IncorporationId && x.ContentOrdinal == selected.ContentOrdinal, Token);
        Assert.Equal(content.Instruction, result.Instruction);
        Assert.Equal(1, result.FulfillmentQuantity);
        Assert.Equal(before.Where(x => x.Id != selected.Id), (await fixture.ReadPreparationWorkAsync(Token)).Where(x => x.Id != selected.Id));
        foreach (var state in await fixture.ReadContentQuantityStatesAsync(Token))
            Assert.Equal(state.IncorporationId == selected.IncorporationId && state.ContentOrdinal == selected.ContentOrdinal ? 2 : 0, state.CancelledQuantity);
        await AssertInvariants();
    }
}
