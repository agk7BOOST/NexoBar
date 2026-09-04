using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryMovementHistoryAuthorizationConcurrencyTests(
    InventoryApiFixture fixture)
{
    [Fact]
    public async Task Stabilized_inventory_operation_completes_read_before_revocation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Authorized history race", "unit", token);
        var actor = await fixture.CreateActorAsync(
            $"history-race-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);
        var capabilityLocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithAuthorization(
            services => new BlockingInventoryAuthorization(
                new InventoryAuthorization(
                    services.GetRequiredService<IAuthenticatedSessionStabilizer>()),
                capabilityLocked,
                releaseRead));
        using var client = application.CreateClient();
        await fixture.LoginAsync(client, actor, token);
        var readTask = InventoryApiFixture.GetMovementHistoryAsync(
            client,
            item.Id.ToString("D"),
            token);
        await capabilityLocked.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var revokeTask = fixture.RevokeResponsibilityAsync(
            actor.IdentityId,
            FunctionalResponsibility.InventoryOperation,
            token);

        try
        {
            Assert.True(await fixture.WaitForDatabaseLockAsync(
                "responsibility_assignments",
                TimeSpan.FromSeconds(10),
                token));
            releaseRead.TrySetResult();
            using var read = await readTask;
            read.EnsureSuccessStatusCode();
            await revokeTask;

            using var afterRevocation = await InventoryApiFixture
                .GetMovementHistoryAsync(
                    client,
                    item.Id.ToString("D"),
                    token);
            Assert.Equal(HttpStatusCode.Forbidden, afterRevocation.StatusCode);
        }
        finally
        {
            releaseRead.TrySetResult();
            await ObserveAsync(readTask);
            await ObserveAsync(revokeTask);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The originating assertion preserves the relevant failure.
        }
    }
}
