using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryMovementHistoryIdentityTests(
    InventoryApiFixture fixture)
{
    [Fact]
    public async Task History_uses_current_actor_operational_name()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync(token);
        var actor = await OperationActorAsync("renamed", token);
        await fixture.LoginAsync(actor, token);
        using (var movement = await fixture.PostEntryAsync(
                   item.Id, Guid.NewGuid(), "1", token))
        {
            movement.EnsureSuccessStatusCode();
        }
        await fixture.ChangeIdentityOperationalNameAsync(
            actor.IdentityId,
            "Current operator name",
            token);

        var result = await ReadAsync(fixture.Client, item.Id, token);

        var returned = Assert.Single(result.Movements);
        Assert.Equal(actor.IdentityId, returned.ActorIdentityId);
        Assert.Equal("Current operator name", returned.ActorOperationalName);
    }

    [Fact]
    public async Task Inactive_historical_actor_still_resolves_for_active_reader()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync(token);
        var historicalActor = await OperationActorAsync("former", token);
        await fixture.LoginAsync(historicalActor, token);
        using (var movement = await fixture.PostEntryAsync(
                   item.Id, Guid.NewGuid(), "1", token))
        {
            movement.EnsureSuccessStatusCode();
        }
        var reader = await OperationActorAsync("active-reader", token);
        await fixture.LoginAsync(reader, token);
        await fixture.DeactivateIdentityAsync(historicalActor.IdentityId, token);

        var result = await ReadAsync(fixture.Client, item.Id, token);

        var returned = Assert.Single(result.Movements);
        Assert.Equal(historicalActor.IdentityId, returned.ActorIdentityId);
        Assert.StartsWith("Inventory actor identity-former-",
            returned.ActorOperationalName,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multiple_historical_actors_are_resolved_by_identity()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync(token);
        var first = await OperationActorAsync("first", token);
        await fixture.LoginAsync(first, token);
        using (var movement = await fixture.PostEntryAsync(
                   item.Id, Guid.NewGuid(), "1", token))
        {
            movement.EnsureSuccessStatusCode();
        }
        var second = await OperationActorAsync("second", token);
        await fixture.LoginAsync(second, token);
        using (var movement = await fixture.PostWasteAsync(
                   item.Id, Guid.NewGuid(), "1", token))
        {
            movement.EnsureSuccessStatusCode();
        }

        var result = await ReadAsync(fixture.Client, item.Id, token);

        Assert.Equal(2, result.Movements.Count);
        Assert.Equal(second.IdentityId, result.Movements[0].ActorIdentityId);
        Assert.StartsWith("Inventory actor identity-second-",
            result.Movements[0].ActorOperationalName,
            StringComparison.Ordinal);
        Assert.Equal(first.IdentityId, result.Movements[1].ActorIdentityId);
        Assert.StartsWith("Inventory actor identity-first-",
            result.Movements[1].ActorOperationalName,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_resolves_distinct_actor_ids_in_one_batch_call()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync(token);
        var actor = await OperationActorAsync("single-batch", token);
        await fixture.LoginAsync(actor, token);
        for (var index = 0; index < 3; index++)
        {
            using var movement = await fixture.PostEntryAsync(
                item.Id, Guid.NewGuid(), "1", token);
            movement.EnsureSuccessStatusCode();
        }

        var lookup = new RecordingIdentityOperationalNameLookup(
            [new IdentityOperationalName(actor.IdentityId, "Batch actor")]);
        await using var application = fixture.CreateApplicationWithIdentityLookup(
            _ => lookup);
        using var client = application.CreateClient();
        await fixture.LoginAsync(client, actor, token);

        var result = await ReadAsync(client, item.Id, token);

        Assert.Equal(3, result.Movements.Count);
        Assert.Equal(1, lookup.CallCount);
        Assert.Equal([actor.IdentityId], lookup.RequestedIdentityIds);
        Assert.All(result.Movements,
            movement => Assert.Equal("Batch actor", movement.ActorOperationalName));
    }

    [Fact]
    public async Task Missing_historical_actor_returns_controlled_internal_error()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Missing actor history", "unit", token);
        await fixture.SetRegisteredStateAsync(item.Id, 1m, 1, token);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            dbContext.InventoryMovements.Add(InventoryMovement.Entry(
                Guid.CreateVersion7(),
                item.Id,
                1,
                1m,
                0m,
                1m,
                DateTimeOffset.UtcNow,
                Guid.CreateVersion7()));
            await dbContext.SaveChangesAsync(token);
        }
        var reader = await OperationActorAsync("missing-reader", token);
        await fixture.LoginAsync(reader, token);

        using var response = await fixture.GetMovementHistoryAsync(item.Id, token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "inventory.movement_history.actor_missing",
            token);
    }

    [Fact]
    public async Task Impossible_persisted_arithmetic_returns_controlled_internal_error()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await EstablishedItemAsync(token);
        var actor = await OperationActorAsync("corruption", token);
        await fixture.LoginAsync(actor, token);
        Guid movementId;
        using (var movement = await fixture.PostEntryAsync(
                   item.Id, Guid.NewGuid(), "1", token))
        {
            movement.EnsureSuccessStatusCode();
            movementId = (await movement.Content
                .ReadFromJsonAsync<InventoryMovementResponse>(token))!.MovementId;
        }
        await fixture.CorruptMovementResultAsync(movementId, 999m, token);

        using var response = await fixture.GetMovementHistoryAsync(item.Id, token);

        await InventoryTestAssertions.AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "inventory.movement_history.state_inconsistent",
            token);
    }

    private async Task<InventoryItem> EstablishedItemAsync(CancellationToken token)
    {
        var item = await fixture.AddItemAsync(
            $"Identity history {Guid.NewGuid():N}", "unit", token);
        await fixture.SetRegisteredStateAsync(item.Id, 10m, 0, token);
        return item;
    }

    private Task<InventoryActor> OperationActorAsync(
        string suffix,
        CancellationToken token) =>
        fixture.CreateActorAsync(
            $"identity-{suffix}-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);

    private static async Task<InventoryMovementHistoryResponse> ReadAsync(
        HttpClient client,
        Guid itemId,
        CancellationToken token)
    {
        using var response = await InventoryApiFixture.GetMovementHistoryAsync(
            client,
            itemId.ToString("D"),
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content
            .ReadFromJsonAsync<InventoryMovementHistoryResponse>(token))!;
    }
}

internal sealed class RecordingIdentityOperationalNameLookup(
    IReadOnlyList<IdentityOperationalName> names) : IIdentityOperationalNameLookup
{
    internal int CallCount { get; private set; }

    internal IReadOnlyList<Guid> RequestedIdentityIds { get; private set; } = [];

    public Task<IReadOnlyList<IdentityOperationalName>> ReadByIdsAsync(
        IReadOnlyCollection<Guid> identityIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        RequestedIdentityIds = identityIds.ToArray();
        return Task.FromResult(names);
    }
}
