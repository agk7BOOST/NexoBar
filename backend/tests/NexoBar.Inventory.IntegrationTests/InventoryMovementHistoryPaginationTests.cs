using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryMovementHistoryPaginationTests(
    InventoryApiFixture fixture)
{
    [Fact]
    public async Task Default_page_is_newest_first_with_limit_fifty()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await LoginOperatorAsync("default-page", token);
        var item = await SeedEntriesAsync(51, actor.IdentityId, token);

        var result = await ReadAsync(item.Id, null, token);

        Assert.Equal(50, result.Movements.Count);
        Assert.Equal(Enumerable.Range(2, 50).Reverse().Select(value => (long)value),
            result.Movements.Select(movement => movement.MovementRevision));
        Assert.Equal(2, result.NextBeforeRevision);
    }

    [Fact]
    public async Task Explicit_limit_and_before_revision_traverse_without_duplicates()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await LoginOperatorAsync("cursor-pages", token);
        var item = await SeedEntriesAsync(5, actor.IdentityId, token);

        var first = await ReadAsync(item.Id, "?limit=2", token);
        var second = await ReadAsync(
            item.Id,
            $"?limit=2&beforeRevision={first.NextBeforeRevision}",
            token);
        var final = await ReadAsync(
            item.Id,
            $"?limit=2&beforeRevision={second.NextBeforeRevision}",
            token);

        Assert.Equal([5L, 4L], Revisions(first));
        Assert.Equal(4, first.NextBeforeRevision);
        Assert.Equal([3L, 2L], Revisions(second));
        Assert.Equal(2, second.NextBeforeRevision);
        Assert.Equal([1L], Revisions(final));
        Assert.Null(final.NextBeforeRevision);
        Assert.Equal(5, first.Movements
            .Concat(second.Movements)
            .Concat(final.Movements)
            .Select(movement => movement.MovementId)
            .Distinct()
            .Count());
    }

    [Fact]
    public async Task Maximum_limit_boundary_is_accepted()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await LoginOperatorAsync("max-page", token);
        var item = await SeedEntriesAsync(51, actor.IdentityId, token);

        var result = await ReadAsync(item.Id, "?limit=100", token);

        Assert.Equal(51, result.Movements.Count);
        Assert.Equal(51, result.Movements[0].MovementRevision);
        Assert.Equal(1, result.Movements[^1].MovementRevision);
        Assert.Null(result.NextBeforeRevision);
    }

    [Fact]
    public async Task Newer_movement_between_pages_does_not_corrupt_backward_traversal()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await LoginOperatorAsync("stable-pages", token);
        var item = await SeedEntriesAsync(5, actor.IdentityId, token);
        var first = await ReadAsync(item.Id, "?limit=2", token);

        using (var newMovement = await fixture.PostEntryAsync(
                   item.Id, Guid.NewGuid(), "1", token))
        {
            newMovement.EnsureSuccessStatusCode();
        }
        var second = await ReadAsync(
            item.Id,
            $"?limit=2&beforeRevision={first.NextBeforeRevision}",
            token);

        Assert.Equal([5L, 4L], Revisions(first));
        Assert.Equal([3L, 2L], Revisions(second));
        Assert.DoesNotContain(second.Movements,
            movement => movement.MovementRevision == 6);
        Assert.Equal(2, second.NextBeforeRevision);
    }

    private async Task<InventoryItem> SeedEntriesAsync(
        int count,
        Guid actorIdentityId,
        CancellationToken token)
    {
        var item = await fixture.AddItemAsync(
            $"Paginated history {Guid.NewGuid():N}", "unit", token);
        await fixture.SetRegisteredStateAsync(item.Id, 0m, 0, token);

        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var trackedItem = await dbContext.InventoryItems.SingleAsync(
            candidate => candidate.Id == item.Id,
            token);
        for (var index = 0; index < count; index++)
        {
            var transition = trackedItem.RecordEntry(1m);
            dbContext.InventoryMovements.Add(InventoryMovement.Entry(
                Guid.CreateVersion7(),
                trackedItem.Id,
                transition.MovementRevision,
                1m,
                transition.PreviousRegisteredQuantity,
                transition.ResultingRegisteredQuantity,
                DateTimeOffset.UtcNow,
                actorIdentityId));
        }

        await dbContext.SaveChangesAsync(token);
        return trackedItem;
    }

    private async Task<InventoryActor> LoginOperatorAsync(
        string suffix,
        CancellationToken token)
    {
        var actor = await fixture.CreateActorAsync(
            $"pagination-{suffix}-{Guid.NewGuid():N}",
            token,
            FunctionalResponsibility.InventoryOperation);
        await fixture.LoginAsync(actor, token);
        return actor;
    }

    private async Task<InventoryMovementHistoryResponse> ReadAsync(
        Guid itemId,
        string? query,
        CancellationToken token)
    {
        using var response = await fixture.GetMovementHistoryAsync(
            itemId,
            token,
            query);
        response.EnsureSuccessStatusCode();
        return (await response.Content
            .ReadFromJsonAsync<InventoryMovementHistoryResponse>(token))!;
    }

    private static long[] Revisions(InventoryMovementHistoryResponse response) =>
        response.Movements
            .Select(movement => movement.MovementRevision)
            .ToArray();
}
