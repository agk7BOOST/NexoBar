using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryItemDomainAndPersistenceTests(InventoryApiFixture fixture)
{
    [Fact]
    public void Creation_trims_normalizes_and_starts_uninitialized()
    {
        var validation = InventoryItem.TryCreate("  Harina  ", "  Kg  ");

        Assert.Null(validation.Error);
        var item = Assert.IsType<InventoryItem>(validation.Item);
        Assert.Equal(7, GetUuidVersion(item.Id));
        Assert.Equal("Harina", item.OperationalName);
        Assert.Equal("HARINA", item.NormalizedOperationalName);
        Assert.Equal("Kg", item.OperationalUnit.Value);
        Assert.Null(item.CurrentRegisteredQuantity);
        Assert.Equal(0, item.MovementRevision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Harina\nIntegral")]
    [InlineData("Harina\rIntegral")]
    public void Empty_or_multiline_name_is_rejected(string? operationalName)
    {
        var validation = InventoryItem.TryCreate(operationalName, "kg");
        Assert.Equal(
            InventoryItemValidationError.OperationalNameInvalid,
            validation.Error);
        Assert.Null(validation.Item);
    }

    [Fact]
    public void Excessively_long_name_is_rejected()
    {
        var validation = InventoryItem.TryCreate(
            new string('a', InventoryItem.OperationalNameMaximumLength + 1),
            "kg");
        Assert.Equal(
            InventoryItemValidationError.OperationalNameInvalid,
            validation.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("k\ng")]
    [InlineData("k\rg")]
    public void Empty_or_multiline_unit_is_rejected(string? operationalUnit)
    {
        var validation = InventoryItem.TryCreate("Harina", operationalUnit);
        Assert.Equal(
            InventoryItemValidationError.OperationalUnitInvalid,
            validation.Error);
        Assert.Null(validation.Item);
    }

    [Fact]
    public void Unit_preserves_visible_case_after_trim()
    {
        var item = InventoryItem.TryCreate("Harina", "  Kg  ").Item!;
        Assert.Equal("Kg", item.OperationalUnit.Value);
    }

    [Fact]
    public void Model_has_no_product_relation_or_lifecycle_state()
    {
        var properties = typeof(InventoryItem)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
            .ToArray();
        var propertyNames = properties
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain("ProductId", propertyNames);
        Assert.DoesNotContain("IsActive", propertyNames);
        Assert.Equal(
            typeof(decimal?),
            Assert.Single(
                properties,
                property => property.Name == "CurrentRegisteredQuantity").PropertyType);
        Assert.Equal(
            typeof(long),
            Assert.Single(
                properties,
                property => property.Name == "MovementRevision").PropertyType);
    }

    [Fact]
    public async Task Exact_fractional_and_negative_quantity_round_trips_without_rounding()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var item = await fixture.AddItemAsync("Harina", "kg", token);
        const decimal expected = -1234.123456789012m;

        await fixture.SetRegisteredStateAsync(item.Id, expected, 9, token);

        var persisted = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal(expected, persisted.CurrentRegisteredQuantity);
        Assert.Equal(9, persisted.MovementRevision);
    }

    [Fact]
    public async Task Normalized_name_is_physically_unique()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await fixture.AddItemAsync("Harina", "kg", token);

        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        dbContext.InventoryItems.Add(
            InventoryItem.TryCreate(" harina ", "bolsa").Item!);
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            dbContext.SaveChangesAsync(token));
    }

    private static int GetUuidVersion(Guid value)
    {
        var bytes = value.ToByteArray(bigEndian: true);
        return bytes[6] >> 4;
    }
}
