using Npgsql;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class IdentityDeleteMigrationTests(IdentitiesAndCapabilitiesFixture fixture)
{
    [Fact]
    public async Task Migration_up_down_and_model_snapshot_match_PostgreSQL()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await fixture.MigrateIdentitiesAndCapabilitiesAsync(
            "20260920130000_AddRecoveryFactorAdministrativeCommand", token);
        Assert.True(await HasActorForeignKeyAsync(token));

        await fixture.MigrateIdentitiesAndCapabilitiesAsync(null, token);
        Assert.False(await HasActorForeignKeyAsync(token));
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    private async Task<bool> HasActorForeignKeyAsync(CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1 FROM pg_constraint
              WHERE conname = 'FK_administrative_command_actor'
                AND connamespace = 'identities_and_capabilities'::regnamespace
            )
            """, connection);
        return (bool)(await command.ExecuteScalarAsync(token))!;
    }
}
