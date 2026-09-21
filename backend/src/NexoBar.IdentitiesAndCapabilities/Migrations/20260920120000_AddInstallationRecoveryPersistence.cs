using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace NexoBar.IdentitiesAndCapabilities.Migrations;

[DbContext(typeof(IdentitiesAndCapabilitiesDbContext))]
[Migration("20260920120000_AddInstallationRecoveryPersistence")]
public partial class AddInstallationRecoveryPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "retry_recovery_factor_verifier",
            schema: "identities_and_capabilities",
            table: "installation_provisioning",
            type: "text",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "extraordinary_general_configuration_recovery_commands",
            schema: "identities_and_capabilities",
            columns: table => new
            {
                command_id = table.Column<Guid>(type: "uuid", nullable: false),
                target_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                login_intent_mode = table.Column<string>(type: "text", nullable: false),
                requested_login_identifier = table.Column<string>(type: "text", nullable: true),
                retry_credential_verifier = table.Column<string>(type: "text", nullable: false),
                recovery_factor_generation = table.Column<int>(type: "integer", nullable: false),
                completed_at = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_extraordinary_general_configuration_recovery_commands",
                    x => x.command_id);
                table.CheckConstraint(
                    "CK_extraordinary_recovery_command_login_intent",
                    "login_intent_mode IN ('PreserveExisting', 'ExplicitIdentifier')");
                table.CheckConstraint(
                    "CK_extraordinary_recovery_command_requested_login",
                    "(login_intent_mode = 'PreserveExisting' AND " +
                    "requested_login_identifier IS NULL) OR " +
                    "(login_intent_mode = 'ExplicitIdentifier' AND " +
                    "requested_login_identifier IS NOT NULL AND " +
                    "length(requested_login_identifier) > 0)");
                table.CheckConstraint(
                    "CK_extraordinary_recovery_command_generation",
                    "recovery_factor_generation > 0");
                table.CheckConstraint(
                    "CK_extraordinary_recovery_command_credential_verifier",
                    "length(retry_credential_verifier) > 0");
            });

        migrationBuilder.CreateTable(
            name: "installation_recovery_state",
            schema: "identities_and_capabilities",
            columns: table => new
            {
                singleton_key = table.Column<short>(type: "smallint", nullable: false),
                recovery_factor_verifier = table.Column<string>(type: "text", nullable: false),
                generation = table.Column<int>(type: "integer", nullable: false),
                established_at = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
                last_rotated_at = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_recovery_state", x => x.singleton_key);
                table.CheckConstraint(
                    "CK_installation_recovery_state_singleton",
                    "singleton_key = 1");
                table.CheckConstraint(
                    "CK_installation_recovery_state_generation",
                    "generation > 0");
                table.CheckConstraint(
                    "CK_installation_recovery_state_rotation_time",
                    "last_rotated_at IS NULL OR last_rotated_at >= established_at");
                table.CheckConstraint(
                    "CK_installation_recovery_state_verifier",
                    "length(recovery_factor_verifier) > 0");
            });

        migrationBuilder.CreateIndex(
            name: "IX_extraordinary_recovery_commands_target_identity_id",
            schema: "identities_and_capabilities",
            table: "extraordinary_general_configuration_recovery_commands",
            column: "target_identity_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "extraordinary_general_configuration_recovery_commands",
            schema: "identities_and_capabilities");

        migrationBuilder.DropTable(
            name: "installation_recovery_state",
            schema: "identities_and_capabilities");

        migrationBuilder.DropColumn(
            name: "retry_recovery_factor_verifier",
            schema: "identities_and_capabilities",
            table: "installation_provisioning");
    }
}
