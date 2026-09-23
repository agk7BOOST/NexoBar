using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.IdentitiesAndCapabilities.Migrations;

[DbContext(typeof(IdentitiesAndCapabilitiesDbContext))]
[Migration("20260923120000_AddEligibleIdentityDelete")]
public partial class AddEligibleIdentityDelete : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_administrative_command_actor",
            schema: "identities_and_capabilities",
            table: "administrative_commands");
        migrationBuilder.DropCheckConstraint(
            name: "CK_administrative_command_kind",
            schema: "identities_and_capabilities",
            table: "administrative_commands");
        migrationBuilder.AddCheckConstraint(
            name: "CK_administrative_command_kind",
            schema: "identities_and_capabilities",
            table: "administrative_commands",
            sql: "command_kind IN ('CreateIdentity', 'ChangeOperationalName', " +
                "'ActivateIdentity', 'DeactivateIdentity', 'DeleteIdentity', " +
                "'SetLocalCredential', 'AssignResponsibility', 'RevokeResponsibility', " +
                "'GrantPreparationEnablement', 'RevokePreparationEnablement', " +
                "'RotateInstallationRecoveryFactor')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A pre-Delete schema cannot represent command actors that were removed.
        // Fail before changing the check constraint rather than silently discard replay.
        migrationBuilder.Sql("""
            DO $$ BEGIN
              IF EXISTS (
                SELECT 1 FROM identities_and_capabilities.administrative_commands c
                LEFT JOIN identities_and_capabilities.identities i ON i.id = c.actor_identity_id
                WHERE i.id IS NULL
              ) THEN
                RAISE EXCEPTION 'Cannot restore actor FK while deleted Identity command history remains';
              END IF;
            END $$;
            """);
        migrationBuilder.DropCheckConstraint(
            name: "CK_administrative_command_kind",
            schema: "identities_and_capabilities",
            table: "administrative_commands");
        migrationBuilder.AddCheckConstraint(
            name: "CK_administrative_command_kind",
            schema: "identities_and_capabilities",
            table: "administrative_commands",
            sql: "command_kind IN ('CreateIdentity', 'ChangeOperationalName', " +
                "'ActivateIdentity', 'DeactivateIdentity', 'SetLocalCredential', " +
                "'AssignResponsibility', 'RevokeResponsibility', " +
                "'GrantPreparationEnablement', 'RevokePreparationEnablement', " +
                "'RotateInstallationRecoveryFactor')");
        migrationBuilder.AddForeignKey(
            name: "FK_administrative_command_actor",
            schema: "identities_and_capabilities",
            table: "administrative_commands",
            column: "actor_identity_id",
            principalSchema: "identities_and_capabilities",
            principalTable: "identities",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }
}
