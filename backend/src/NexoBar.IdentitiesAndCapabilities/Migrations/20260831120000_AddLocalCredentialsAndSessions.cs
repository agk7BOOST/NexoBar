using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.IdentitiesAndCapabilities.Migrations;

public partial class AddLocalCredentialsAndSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "local_credentials",
            schema: "identities_and_capabilities",
            columns: table => new
            {
                identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                login_identifier = table.Column<string>(type: "text", nullable: false),
                normalized_login_identifier = table.Column<string>(
                    type: "text",
                    nullable: false),
                secret_verifier = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_local_credentials", x => x.identity_id);
                table.CheckConstraint(
                    "CK_local_credential_login_identifier",
                    "login_identifier = btrim(login_identifier) AND " +
                    "length(login_identifier) > 0");
                table.CheckConstraint(
                    "CK_local_credential_normalized_login",
                    "normalized_login_identifier = " +
                    "btrim(normalized_login_identifier) AND " +
                    "length(normalized_login_identifier) > 0");
                table.CheckConstraint(
                    "CK_local_credential_secret_verifier",
                    "length(secret_verifier) > 0");
                table.ForeignKey(
                    name: "FK_local_credential_identity",
                    column: x => x.identity_id,
                    principalSchema: "identities_and_capabilities",
                    principalTable: "identities",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "sessions",
            schema: "identities_and_capabilities",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                created_at = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
                last_activity_at = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
                absolute_expires_at = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
                revoked_at = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_sessions", x => x.id);
                table.CheckConstraint(
                    "CK_session_absolute_expiration",
                    "absolute_expires_at > created_at");
                table.CheckConstraint(
                    "CK_session_last_activity",
                    "last_activity_at >= created_at AND " +
                    "last_activity_at < absolute_expires_at");
                table.CheckConstraint(
                    "CK_session_revoked_at",
                    "revoked_at IS NULL OR revoked_at >= created_at");
                table.ForeignKey(
                    name: "FK_session_identity",
                    column: x => x.identity_id,
                    principalSchema: "identities_and_capabilities",
                    principalTable: "identities",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "UX_local_credential_normalized_login",
            schema: "identities_and_capabilities",
            table: "local_credentials",
            column: "normalized_login_identifier",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_session_identity_id",
            schema: "identities_and_capabilities",
            table: "sessions",
            column: "identity_id");

        migrationBuilder.CreateIndex(
            name: "UX_session_token_hash",
            schema: "identities_and_capabilities",
            table: "sessions",
            column: "token_hash",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "local_credentials",
            schema: "identities_and_capabilities");

        migrationBuilder.DropTable(
            name: "sessions",
            schema: "identities_and_capabilities");
    }
}
