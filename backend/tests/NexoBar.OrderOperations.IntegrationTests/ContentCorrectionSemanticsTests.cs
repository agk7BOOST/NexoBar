using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ContentCorrectionSemanticsTests(OrderOperationsApiFixture fixture)
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    public async Task Exact_correction_preserves_confirmation_and_delivery(bool prepared, int quantity)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = prepared ? await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 3, 0, token)
            : await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        var original = await fixture.ReadConfirmedContentsAsync(token);
        var workBefore = await fixture.ReadPreparationWorkAsync(token);
        var before = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, token);
        using var response = await ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), quantity, token);
        var result = await ContentCorrectionTestSupport.SuccessAsync(response, token);
        Assert.Equal(3, result.ConfirmedQuantity);
        Assert.Equal(0, result.PreviousRemovedByCorrectionQuantity);
        Assert.Equal(quantity, result.ResultingRemovedByCorrectionQuantity);
        Assert.Equal(3, result.PreviousFulfillmentQuantity);
        Assert.Equal(3 - quantity, result.ResultingFulfillmentQuantity);
        Assert.Equal(original, await fixture.ReadConfirmedContentsAsync(token));
        Assert.Equal(0, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
        if (prepared)
        {
            var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
            Assert.Equal(workBefore.Single().Id, work.Id);
            Assert.Equal(workBefore.Single().PreparationResponsibilityId, work.PreparationResponsibilityId);
            Assert.Equal(3 - quantity, work.TotalQuantity);
            Assert.Equal(3 - quantity, work.PendingQuantity);
            Assert.Equal(0, work.InPreparationQuantity);
            Assert.Equal(0, work.ReadyQuantity);
        }
        var after = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, token);
        Assert.Equal(before.FunctionalAmount, after.FunctionalAmount);
        Assert.Equal(quantity == 3, after.IsLiquidationEligible);
        using var read = await fixture.OrderOperationsClient.GetAsync($"/api/order-operations/orders/{target.OperationalReference}/delivery", token);
        read.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
        var item = json.RootElement.GetProperty("contents")[0];
        Assert.Equal(target.ContentOrdinal, item.GetProperty("contentOrdinal").GetInt32());
        Assert.Equal(3, item.GetProperty("confirmedQuantity").GetInt32());
        Assert.Equal(quantity, item.GetProperty("removedByCorrectionQuantity").GetInt32());
        Assert.Equal(3 - quantity, item.GetProperty("currentFulfillmentQuantity").GetInt32());
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var history = await db.ContentCorrectionHistory.SingleAsync(token);
        Assert.Equal("ContentQuantityCorrected", history.EventKind);
        Assert.Equal(fixture.DefaultOrderOperationsActor.IdentityId, history.ActorIdentityId);
        Assert.Equal(result.HistoryId, history.Id);
        Assert.Equal(result.OccurredAt, history.OccurredAt);
        Assert.Equal(result.ConfirmedQuantity, history.ConfirmedQuantity);
        Assert.Equal(result.PreviousRemovedByCorrectionQuantity, history.PreviousRemovedByCorrectionQuantity);
        Assert.Equal(result.ResultingRemovedByCorrectionQuantity, history.ResultingRemovedByCorrectionQuantity);
        Assert.Equal(result.PreviousFulfillmentQuantity, history.PreviousFulfillmentQuantity);
        Assert.Equal(result.ResultingFulfillmentQuantity, history.ResultingFulfillmentQuantity);
        Assert.Single(await db.ConfirmationHistory.ToArrayAsync(token));
        await ContentCorrectionTestSupport.CountsAsync(fixture, 1, token);
    }

    [Theory]
    [InlineData(false, 1, 200)]
    [InlineData(false, 2, 409)]
    [InlineData(true, 1, 200)]
    [InlineData(true, 2, 409)]
    [InlineData(true, 0, 400)]
    [InlineData(false, -1, 400)]
    [InlineData(true, int.MaxValue, 409)]
    public async Task Only_exact_eligible_quantity_is_removed(bool prepared, int quantity, int status)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = prepared ? await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 3, 2, token)
            : await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 2, token);
        using var response = await ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), quantity, token);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(2, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
        var order = await LiquidationTestSupport.ReadOrderAsync(fixture.Client, target.OperationalReference, token);
        Assert.Equal(prepared ? "14" : "10", order.FunctionalAmount);
        Assert.Equal(status == 200, order.IsLiquidationEligible);
        if (prepared)
        {
            var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
            Assert.Equal(2, work.ReadyQuantity);
            Assert.Equal(status == 200 ? 0 : 1, work.PendingQuantity);
        }
        await ContentCorrectionTestSupport.CountsAsync(fixture, status == 200 ? 1 : 0, token);
    }

    [Theory]
    [InlineData("content_quantity_states")]
    [InlineData("delivery_states")]
    [InlineData("preparation_work")]
    public async Task Missing_state_is_inconsistency_without_repair(string table)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreatePreparedAsync(fixture, 3, 0, token);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var sql = connection.CreateCommand();
        sql.CommandText = $"DELETE FROM order_operations.{table}";
        await sql.ExecuteNonQueryAsync(token);
        using var response = await ContentCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        await ContentCorrectionTestSupport.ProblemAsync(response, HttpStatusCode.InternalServerError, "state_inconsistent", token);
        await ContentCorrectionTestSupport.CountsAsync(fixture, 0, token);
    }
}
