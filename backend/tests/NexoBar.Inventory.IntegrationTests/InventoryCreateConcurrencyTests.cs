using System.Net;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryCreateConcurrencyTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task Concurrent_equivalent_names_commit_at_most_one_item()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "concurrent-name",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(actor, token);
        var antiforgery = await fixture.GetAntiforgeryTokenAsync(token);

        var responses = await Task.WhenAll(
            fixture.PostItemAsync(
                Guid.NewGuid(),
                "Harina",
                "kg",
                token,
                antiforgery),
            fixture.PostItemAsync(
                Guid.NewGuid(),
                " harina ",
                "bolsa",
                token,
                antiforgery));
        try
        {
            Assert.Equal(
                1,
                responses.Count(response => response.StatusCode == HttpStatusCode.Created));
            var conflict = Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.Conflict);
            await InventoryTestAssertions.AssertProblemAsync(
                conflict,
                HttpStatusCode.Conflict,
                "inventory.item.operational_name_conflict",
                token);
            Assert.Equal((1, 1), await fixture.CountInventoryAsync(token));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}
