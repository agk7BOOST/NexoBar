using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.IdentitiesAndCapabilities.Migrations;

[DbContext(typeof(IdentitiesAndCapabilitiesDbContext))]
[Migration("20260920130000_AddRecoveryFactorAdministrativeCommand")]
public partial class AddRecoveryFactorAdministrativeCommand : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_administrative_command_kind",
            schema: "identities_and_capabilities",
            table: "administrative_commands");
        migrationBuilder.DropCheckConstraint(
            name: "CK_administrative_command_secret_intent",
            schema: "identities_and_capabilities",
            table: "administrative_commands");
        AddRecoveryFactorConstraints(migrationBuilder);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_administrative_command_kind",
            schema: "identities_and_capabilities",
            table: "administrative_commands");
        migrationBuilder.DropCheckConstraint(
            name: "CK_administrative_command_secret_intent",
            schema: "identities_and_capabilities",
            table: "administrative_commands");
        migrationBuilder.Sql(
            "DELETE FROM identities_and_capabilities.administrative_commands " +
            "WHERE command_kind = 'RotateInstallationRecoveryFactor'");
        AddOriginalConstraints(migrationBuilder);
    }

    private static void AddRecoveryFactorConstraints(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddCheckConstraint(
            name: "CK_administrative_command_kind",
            schema: "identities_and_capabilities",
            table: "administrative_commands",
            sql: "command_kind IN (" +
                "'CreateIdentity', 'ChangeOperationalName', 'ActivateIdentity', " +
                "'DeactivateIdentity', 'SetLocalCredential', 'AssignResponsibility', " +
                "'RevokeResponsibility', 'GrantPreparationEnablement', " +
                "'RevokePreparationEnablement', 'RotateInstallationRecoveryFactor')");
        migrationBuilder.AddCheckConstraint(
            name: "CK_administrative_command_secret_intent",
            schema: "identities_and_capabilities",
            table: "administrative_commands",
            sql: "(command_kind IN ('SetLocalCredential', " +
                "'RotateInstallationRecoveryFactor') AND intent_secret_verifier IS NOT NULL) " +
                "OR (command_kind NOT IN ('SetLocalCredential', " +
                "'RotateInstallationRecoveryFactor') AND intent_secret_verifier IS NULL)");
    }

    private static void AddOriginalConstraints(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddCheckConstraint(
            name: "CK_administrative_command_kind",
            schema: "identities_and_capabilities",
            table: "administrative_commands",
            sql: "command_kind IN (" +
                "'CreateIdentity', 'ChangeOperationalName', 'ActivateIdentity', " +
                "'DeactivateIdentity', 'SetLocalCredential', 'AssignResponsibility', " +
                "'RevokeResponsibility', 'GrantPreparationEnablement', " +
                "'RevokePreparationEnablement')");
        migrationBuilder.AddCheckConstraint(
            name: "CK_administrative_command_secret_intent",
            schema: "identities_and_capabilities",
            table: "administrative_commands",
            sql: "(command_kind = 'SetLocalCredential' AND " +
                "intent_secret_verifier IS NOT NULL) OR " +
                "(command_kind <> 'SetLocalCredential' AND " +
                "intent_secret_verifier IS NULL)");
    }
}
