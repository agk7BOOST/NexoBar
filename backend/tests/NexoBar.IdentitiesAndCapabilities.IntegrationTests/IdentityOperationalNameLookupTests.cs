using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class IdentityOperationalNameLookupTests(
    IdentitiesAndCapabilitiesFixture fixture)
{
    [Fact]
    public async Task Batch_lookup_returns_active_and_inactive_identities_once()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var active = await fixture.CreateIdentityAsync("Active operator", true, token);
        var inactive = await fixture.CreateIdentityAsync("Former operator", false, token);

        await using var scope = fixture.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<IIdentityOperationalNameLookup>()
            .ReadByIdsAsync(
                [active.Id, inactive.Id, active.Id],
                token);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, value =>
            value == new IdentityOperationalName(active.Id, "Active operator"));
        Assert.Contains(result, value =>
            value == new IdentityOperationalName(inactive.Id, "Former operator"));
    }

    [Fact]
    public async Task Batch_lookup_returns_current_name_and_omits_missing_identity()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Original name", true, token);
        var missingId = Guid.CreateVersion7();

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
            var persisted = await dbContext.Identities.SingleAsync(
                candidate => candidate.Id == identity.Id,
                token);
            persisted.ChangeOperationalName("Current name");
            await dbContext.SaveChangesAsync(token);
        }

        await using var lookupScope = fixture.Services.CreateAsyncScope();
        var result = await lookupScope.ServiceProvider
            .GetRequiredService<IIdentityOperationalNameLookup>()
            .ReadByIdsAsync([identity.Id, missingId], token);

        var returned = Assert.Single(result);
        Assert.Equal(identity.Id, returned.IdentityId);
        Assert.Equal("Current name", returned.OperationalName);
    }
}
