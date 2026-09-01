using System.Net;
using System.Net.Http.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryReconciliationAtomicityTests(InventoryApiFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Movement_or_command_failure_rolls_back_state_history_and_command(
        bool failMovement)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        var actor = await fixture.CreateActorAsync(
            $"atomicity-{failMovement}-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, token);
        using var countResponse = await fixture.PostCountAsync(
            item.Id, Guid.NewGuid(), "4", token);
        var count = (await countResponse.Content
            .ReadFromJsonAsync<CountObservationResponse>(token))!;
        if (failMovement)
        {
            await fixture.SetMovementFailureAsync(true, token);
        }
        else
        {
            await fixture.SetMovementCommandFailureAsync(true, token);
        }

        try
        {
            using var failed = await fixture.PostReconcileAsync(
                item.Id, Guid.NewGuid(), count.CountObservationId, token);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            var unchanged = await fixture.ReadItemAsync(item.Id, token);
            Assert.Null(unchanged.CurrentRegisteredQuantity);
            Assert.Equal(0, unchanged.MovementRevision);
            Assert.Equal((1, 1, 0, 0), await fixture.ReadInventoryEffectCountsAsync(token));
        }
        finally
        {
            if (failMovement)
            {
                await fixture.SetMovementFailureAsync(false, token);
            }
            else
            {
                await fixture.SetMovementCommandFailureAsync(false, token);
            }
        }
    }
}
