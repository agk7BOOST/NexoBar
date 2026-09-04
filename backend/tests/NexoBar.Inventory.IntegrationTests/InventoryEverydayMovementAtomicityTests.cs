using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryEverydayMovementAtomicityTests(
    InventoryApiFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Movement_or_command_failure_rolls_back_state_history_and_command(
        bool failCommand)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Atomic item", "kg", token);
        await fixture.SetRegisteredStateAsync(item.Id, 10, 7, token);
        var actor = await fixture.CreateActorAsync(
            $"atomic-{failCommand}-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, token);
        if (failCommand)
        {
            await fixture.SetMovementCommandFailureAsync(true, token);
        }
        else
        {
            await fixture.SetMovementFailureAsync(true, token);
        }

        try
        {
            using var response = await fixture.PostEntryAsync(
                item.Id, Guid.NewGuid(), "2", token);
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError,
                response.StatusCode);
        }
        finally
        {
            if (failCommand)
            {
                await fixture.SetMovementCommandFailureAsync(false, token);
            }
            else
            {
                await fixture.SetMovementFailureAsync(false, token);
            }
        }

        var persisted = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(10, persisted.CurrentRegisteredQuantity);
        Assert.Equal(7, persisted.MovementRevision);
        Assert.Empty(await fixture.ReadMovementsAsync(token));
        Assert.Equal(0, (await fixture.ReadInventoryEffectCountsAsync(token))
            .MovementCommands);
    }
}
