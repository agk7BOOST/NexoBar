using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OperationalConfiguration.Migrations;

public partial class InitialOperationalConfiguration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "operational_configuration");

        migrationBuilder.CreateTable(
            name: "preparation_responsibilities",
            schema: "operational_configuration",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                operational_name = table.Column<string>(type: "text", nullable: false),
                normalized_operational_name = table.Column<string>(
                    type: "text",
                    nullable: false,
                    computedColumnSql: "lower(operational_name)",
                    stored: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_operational_configuration_preparation_responsibilities",
                    x => x.id);
                table.CheckConstraint(
                    "CK_op_config_preparation_responsibilities_name_not_empty",
                    "length(btrim(operational_name)) > 0");
            });

        migrationBuilder.CreateTable(
            name: "preparation_responsibility_creation_commands",
            schema: "operational_configuration",
            columns: table => new
            {
                idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                intent_operational_name = table.Column<string>(
                    type: "text", nullable: false),
                result_responsibility_id = table.Column<Guid>(
                    type: "uuid", nullable: false),
                result_operational_name = table.Column<string>(
                    type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_op_config_preparation_responsibility_creation_commands",
                    x => x.idempotency_key);
                table.CheckConstraint(
                    "CK_operational_configuration_responsibility_cmd_intent_name",
                    "length(btrim(intent_operational_name)) > 0");
                table.CheckConstraint(
                    "CK_operational_configuration_responsibility_cmd_result_name",
                    "length(btrim(result_operational_name)) > 0");
                table.ForeignKey(
                    name: "FK_op_config_prep_responsibility_creation_cmd_responsibility",
                    column: x => x.result_responsibility_id,
                    principalSchema: "operational_configuration",
                    principalTable: "preparation_responsibilities",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "UX_operational_configuration_responsibilities_normalized_name",
            schema: "operational_configuration",
            table: "preparation_responsibilities",
            column: "normalized_operational_name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UX_op_config_preparation_responsibility_creation_cmd_result",
            schema: "operational_configuration",
            table: "preparation_responsibility_creation_commands",
            column: "result_responsibility_id",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "preparation_responsibility_creation_commands",
            schema: "operational_configuration");
        migrationBuilder.DropTable(
            name: "preparation_responsibilities",
            schema: "operational_configuration");
    }
}
