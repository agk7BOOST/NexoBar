using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.IdentitiesAndCapabilities.Migrations;

public partial class AddIdentityAdministrationCommands : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "administrative_commands",
            schema: "identities_and_capabilities",
            columns: table => new
            {
                idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                command_kind = table.Column<string>(type: "text", nullable: false),
                intent_fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                intent_secret_verifier = table.Column<string>(
                    type: "text",
                    nullable: true),
                result_payload = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_administrative_commands", x => x.idempotency_key);
                table.CheckConstraint(
                    "CK_administrative_command_kind",
                    "command_kind IN (" +
                    "'CreateIdentity', " +
                    "'ChangeOperationalName', " +
                    "'ActivateIdentity', " +
                    "'DeactivateIdentity', " +
                    "'SetLocalCredential', " +
                    "'AssignResponsibility', " +
                    "'RevokeResponsibility', " +
                    "'GrantPreparationEnablement', " +
                    "'RevokePreparationEnablement')");
                table.CheckConstraint(
                    "CK_administrative_command_secret_intent",
                    "(command_kind = 'SetLocalCredential' AND " +
                    "intent_secret_verifier IS NOT NULL) OR " +
                    "(command_kind <> 'SetLocalCredential' AND " +
                    "intent_secret_verifier IS NULL)");
                table.ForeignKey(
                    name: "FK_administrative_command_actor",
                    column: x => x.actor_identity_id,
                    principalSchema: "identities_and_capabilities",
                    principalTable: "identities",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_administrative_commands_actor_identity_id",
            schema: "identities_and_capabilities",
            table: "administrative_commands",
            column: "actor_identity_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "administrative_commands",
            schema: "identities_and_capabilities");
    }
}
