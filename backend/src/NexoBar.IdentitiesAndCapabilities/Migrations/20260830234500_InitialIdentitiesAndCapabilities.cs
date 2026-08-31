using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.IdentitiesAndCapabilities.Migrations;

public partial class InitialIdentitiesAndCapabilities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "identities_and_capabilities");

        migrationBuilder.CreateTable(
            name: "identities",
            schema: "identities_and_capabilities",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                operational_name = table.Column<string>(type: "text", nullable: false),
                normalized_operational_name = table.Column<string>(
                    type: "text",
                    nullable: false,
                    computedColumnSql: "lower(operational_name)",
                    stored: true),
                is_active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_identities_and_capabilities_identities",
                    x => x.id);
                table.CheckConstraint(
                    "CK_identity_operational_name_trimmed_not_empty",
                    "operational_name = btrim(operational_name) AND " +
                    "length(operational_name) > 0");
            });

        migrationBuilder.CreateTable(
            name: "preparation_enablements",
            schema: "identities_and_capabilities",
            columns: table => new
            {
                identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                preparation_responsibility_id = table.Column<Guid>(
                    type: "uuid",
                    nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_preparation_enablements",
                    x => new { x.identity_id, x.preparation_responsibility_id });
                table.ForeignKey(
                    name: "FK_preparation_enablements_identity",
                    column: x => x.identity_id,
                    principalSchema: "identities_and_capabilities",
                    principalTable: "identities",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "responsibility_assignments",
            schema: "identities_and_capabilities",
            columns: table => new
            {
                identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                responsibility_code = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_responsibility_assignments",
                    x => new { x.identity_id, x.responsibility_code });
                table.CheckConstraint(
                    "CK_responsibility_assignment_code",
                    "responsibility_code IN (" +
                    "'OrderOperationsAndBasicClosure', " +
                    "'OperationalIntervention', " +
                    "'Preparation', " +
                    "'CatalogConfiguration', " +
                    "'InventoryOperation', " +
                    "'InventoryConfiguration', " +
                    "'GeneralConfiguration')");
                table.ForeignKey(
                    name: "FK_responsibility_assignments_identity",
                    column: x => x.identity_id,
                    principalSchema: "identities_and_capabilities",
                    principalTable: "identities",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_identity_normalized_operational_name",
            schema: "identities_and_capabilities",
            table: "identities",
            column: "normalized_operational_name");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "preparation_enablements",
            schema: "identities_and_capabilities");

        migrationBuilder.DropTable(
            name: "responsibility_assignments",
            schema: "identities_and_capabilities");

        migrationBuilder.DropTable(
            name: "identities",
            schema: "identities_and_capabilities");
    }
}
