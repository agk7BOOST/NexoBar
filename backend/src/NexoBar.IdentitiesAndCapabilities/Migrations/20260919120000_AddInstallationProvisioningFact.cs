using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.IdentitiesAndCapabilities.Migrations;

[DbContext(typeof(IdentitiesAndCapabilitiesDbContext))]
[Migration("20260919120000_AddInstallationProvisioningFact")]
public partial class AddInstallationProvisioningFact : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "installation_provisioning",
            schema: "identities_and_capabilities",
            columns: table => new
            {
                singleton_key = table.Column<short>(type: "smallint", nullable: false),
                origin = table.Column<string>(type: "text", nullable: false),
                completed_at = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: true),
                provisioning_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                initial_identity_id = table.Column<Guid>(type: "uuid", nullable: true),
                retry_intent_fingerprint = table.Column<byte[]>(type: "bytea", nullable: true),
                retry_secret_verifier = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_provisioning", x => x.singleton_key);
                table.CheckConstraint(
                    "CK_installation_provisioning_singleton",
                    "singleton_key = 1");
                table.CheckConstraint(
                    "CK_installation_provisioning_origin",
                    "origin IN ('InitialProvisioning', 'LegacyBackfill')");
                table.CheckConstraint(
                    "CK_installation_provisioning_initial_fields",
                    "(origin = 'InitialProvisioning' AND " +
                    "completed_at IS NOT NULL AND " +
                    "provisioning_command_id IS NOT NULL AND " +
                    "initial_identity_id IS NOT NULL AND " +
                    "retry_intent_fingerprint IS NOT NULL AND " +
                    "octet_length(retry_intent_fingerprint) > 0 AND " +
                    "retry_secret_verifier IS NOT NULL AND " +
                    "length(retry_secret_verifier) > 0) OR " +
                    "(origin = 'LegacyBackfill' AND " +
                    "completed_at IS NULL AND " +
                    "provisioning_command_id IS NULL AND " +
                    "initial_identity_id IS NULL AND " +
                    "retry_intent_fingerprint IS NULL AND " +
                    "retry_secret_verifier IS NULL)");
            });

        migrationBuilder.Sql(
            """
            INSERT INTO identities_and_capabilities.installation_provisioning (
                singleton_key,
                origin)
            SELECT 1, 'LegacyBackfill'
            WHERE EXISTS (
                SELECT 1
                FROM identities_and_capabilities.identities)
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "installation_provisioning",
            schema: "identities_and_capabilities");
    }
}
