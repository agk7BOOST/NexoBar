using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using Npgsql;

namespace NexoBar.Catalog.IntegrationTests;

[Collection(CatalogApiCollection.Name)]
public sealed class ProductDeleteMigrationTests(CatalogApiFixture fixture)
{
    [Fact]
    public async Task Product_Delete_migration_round_trips_and_model_matches_snapshot()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetCatalogAsync(token);
        const string previous = "20260922050000_AddProductAvailabilityChangeCommands";
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database;
            Assert.False(database.HasPendingModelChanges());
            await database.GetService<IMigrator>().MigrateAsync(previous, cancellationToken: token);
        }

        Assert.False(await HasDeleteTableAsync(token));
        Assert.Equal(8, await ProductForeignKeyCountAsync(token));

        await using (var scope = fixture.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CatalogDbContext>()
                .Database.MigrateAsync(token);

        Assert.True(await HasDeleteTableAsync(token));
        Assert.Equal(0, await ProductForeignKeyCountAsync(token));

        await using (var scope = fixture.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CatalogDbContext>()
                .Database.GetService<IMigrator>()
                .MigrateAsync(previous, cancellationToken: token);

        Assert.False(await HasDeleteTableAsync(token));
        Assert.Equal(8, await ProductForeignKeyCountAsync(token));

        await using var finalScope = fixture.Services.CreateAsyncScope();
        await finalScope.ServiceProvider.GetRequiredService<CatalogDbContext>()
            .Database.MigrateAsync(token);
    }

    private async Task<bool> HasDeleteTableAsync(CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('catalog.product_delete_commands') IS NOT NULL";
        return (bool)(await command.ExecuteScalarAsync(token))!;
    }

    private async Task<int> ProductForeignKeyCountAsync(CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM pg_constraint
            WHERE connamespace = 'catalog'::regnamespace
              AND contype = 'f' AND confrelid = 'catalog.products'::regclass
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }
}
