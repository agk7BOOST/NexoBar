using Microsoft.EntityFrameworkCore;
using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class IdentitiesAndCapabilitiesPersistenceTests(
    IdentitiesAndCapabilitiesFixture fixture)
{
    [Fact]
    public async Task Identity_uses_uuid_v7_trims_name_and_preserves_explicit_state()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        var active = await fixture.CreateIdentityAsync("  Ana  ", true, token);
        var inactive = await fixture.CreateIdentityAsync("Beto", false, token);

        Assert.Equal(7, UuidVersion(active.Id));
        Assert.Equal(7, UuidVersion(inactive.Id));
        Assert.Equal("Ana", active.OperationalName);
        Assert.Equal("ana", active.NormalizedOperationalName);
        Assert.True(active.IsActive);
        Assert.False(inactive.IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Identity_rejects_blank_operational_name(string operationalName)
    {
        Assert.Throws<ArgumentException>(() => new Identity(operationalName, true));
    }

    [Fact]
    public async Task Normalized_operational_name_is_not_functionally_unique()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        await fixture.CreateIdentityAsync("Alex", true, token);
        await fixture.CreateIdentityAsync("alex", true, token);

        var identities = await fixture.ReadIdentitiesAsync(token);
        Assert.Equal(2, identities.Length);
        Assert.All(identities, identity =>
            Assert.Equal("alex", identity.NormalizedOperationalName));
    }

    [Fact]
    public async Task Responsibilities_are_exactly_the_seven_closed_codes()
    {
        var expected = new[]
        {
            "OrderOperationsAndBasicClosure",
            "OperationalIntervention",
            "Preparation",
            "CatalogConfiguration",
            "InventoryOperation",
            "InventoryConfiguration",
            "GeneralConfiguration"
        };

        Assert.Equal(expected, Enum.GetNames<FunctionalResponsibility>());

        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        await using var connection = await fixture.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO identities_and_capabilities.responsibility_assignments
                (identity_id, responsibility_code)
            VALUES (@identity_id, 'UnknownResponsibility')
            """;
        command.Parameters.AddWithValue("identity_id", identity.Id);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_responsibility_assignment_code", exception.ConstraintName);
    }

    [Fact]
    public async Task Responsibility_pairs_are_unique_and_support_expected_cardinality()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var first = await fixture.CreateIdentityAsync("Ana", true, token);
        var second = await fixture.CreateIdentityAsync("Beto", true, token);

        await fixture.InsertAssignmentAsync(
            first.Id, FunctionalResponsibility.Preparation, token);
        await fixture.InsertAssignmentAsync(
            first.Id, FunctionalResponsibility.GeneralConfiguration, token);
        await fixture.InsertAssignmentAsync(
            second.Id, FunctionalResponsibility.Preparation, token);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.InsertAssignmentAsync(
                first.Id, FunctionalResponsibility.Preparation, token));
        Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(3, await fixture.CountAssignmentsAsync(token));
    }

    [Fact]
    public async Task Responsibility_assignment_cannot_be_orphaned()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.InsertAssignmentAsync(
                Guid.NewGuid(), FunctionalResponsibility.Preparation, token));
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
        Assert.Equal("FK_responsibility_assignments_identity", postgres.ConstraintName);
    }

    [Fact]
    public async Task Preparation_enablements_support_many_destinations_and_identities()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var first = await fixture.CreateIdentityAsync("Ana", true, token);
        var second = await fixture.CreateIdentityAsync("Beto", true, token);
        var kitchen = await fixture.CreatePreparationResponsibilityAsync("Cocina", token);
        var bar = await fixture.CreatePreparationResponsibilityAsync("Barra", token);

        Assert.Equal(
            GrantPreparationEnablementOutcome.Granted,
            await fixture.GrantEnablementAsync(first.Id, kitchen, token));
        Assert.Equal(
            GrantPreparationEnablementOutcome.Granted,
            await fixture.GrantEnablementAsync(first.Id, bar, token));
        Assert.Equal(
            GrantPreparationEnablementOutcome.Granted,
            await fixture.GrantEnablementAsync(second.Id, kitchen, token));

        Assert.Equal(3, await fixture.CountEnablementsAsync(token));
        Assert.Equal(0, await fixture.CountAssignmentsAsync(token));
    }

    [Fact]
    public async Task Enablement_requires_existing_preparation_responsibility_via_lookup()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);

        var outcome = await fixture.GrantEnablementAsync(
            identity.Id,
            Guid.NewGuid(),
            token);

        Assert.Equal(
            GrantPreparationEnablementOutcome.PreparationResponsibilityNotFound,
            outcome);
        Assert.Equal(0, await fixture.CountEnablementsAsync(token));
    }

    [Fact]
    public async Task Duplicate_grant_is_rejected_and_exact_revoke_is_deterministic()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        var kitchen = await fixture.CreatePreparationResponsibilityAsync("Cocina", token);

        Assert.Equal(
            GrantPreparationEnablementOutcome.Granted,
            await fixture.GrantEnablementAsync(identity.Id, kitchen, token));
        Assert.Equal(
            GrantPreparationEnablementOutcome.AlreadyGranted,
            await fixture.GrantEnablementAsync(identity.Id, kitchen, token));
        Assert.Equal(1, await fixture.CountEnablementsAsync(token));
        Assert.True(await fixture.RevokeEnablementAsync(identity.Id, kitchen, token));
        Assert.False(await fixture.RevokeEnablementAsync(identity.Id, kitchen, token));
    }

    [Fact]
    public async Task Preparation_enablement_pair_is_physically_unique()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        var kitchen = await fixture.CreatePreparationResponsibilityAsync("Cocina", token);
        await fixture.InsertEnablementAsync(identity.Id, kitchen, token);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.InsertEnablementAsync(identity.Id, kitchen, token));
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("PK_preparation_enablements", postgres.ConstraintName);
    }

    [Fact]
    public async Task Preparation_enablement_cannot_be_orphaned()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.InsertEnablementAsync(Guid.NewGuid(), Guid.NewGuid(), token));
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
        Assert.Equal("FK_preparation_enablements_identity", postgres.ConstraintName);
    }

    [Fact]
    public async Task Enablement_has_no_cross_module_foreign_key()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await using var connection = await fixture.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*)
            FROM pg_constraint
            WHERE conrelid =
                    'identities_and_capabilities.preparation_enablements'::regclass
              AND confrelid =
                    'operational_configuration.preparation_responsibilities'::regclass
            """;

        Assert.Equal(0L, Assert.IsType<long>(await command.ExecuteScalarAsync(token)));
    }

    [Fact]
    public async Task Identity_deletion_is_possible_without_relations_and_restricted_with_them()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var unused = await fixture.CreateIdentityAsync("Sin uso", false, token);
        await fixture.RemoveIdentityAsync(unused.Id, token);
        Assert.Empty(await fixture.ReadIdentitiesAsync(token));

        var assigned = await fixture.CreateIdentityAsync("Asignada", true, token);
        await fixture.InsertAssignmentAsync(
            assigned.Id, FunctionalResponsibility.GeneralConfiguration, token);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.RemoveIdentityAsync(assigned.Id, token));
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
    }

    [Fact]
    public async Task Physical_identifiers_do_not_exceed_postgresql_limit()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await fixture.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT max(length(identifier))
            FROM (
                SELECT nspname AS identifier
                FROM pg_namespace
                WHERE nspname = 'identities_and_capabilities'
                UNION ALL
                SELECT relname
                FROM pg_class
                WHERE relnamespace =
                    'identities_and_capabilities'::regnamespace
                UNION ALL
                SELECT conname
                FROM pg_constraint
                WHERE connamespace =
                    'identities_and_capabilities'::regnamespace
            ) identifiers
            """;

        var maximum = Assert.IsType<int>(await command.ExecuteScalarAsync(token));
        Assert.InRange(maximum, 1, 63);
    }

    [Fact]
    public async Task Model_has_no_pending_changes()
    {
        Assert.False(await fixture.HasPendingModelChangesAsync());
    }

    private static int UuidVersion(Guid value)
    {
        var bytes = value.ToByteArray(bigEndian: true);
        return bytes[6] >> 4;
    }
}
