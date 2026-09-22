using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoBar.OperationalConfiguration.Migrations;

[DbContext(typeof(OperationalConfigurationDbContext))]
[Migration("20260922130000_AddOperationalContexts")]
public partial class AddOperationalContexts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "contexts",
            schema: "operational_configuration",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                operational_name = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operational_configuration_contexts", x => x.id);
                table.CheckConstraint("CK_operational_configuration_contexts_name_not_empty", "length(btrim(operational_name)) > 0");
            });
        migrationBuilder.Sql("ALTER TABLE operational_configuration.contexts ADD COLUMN normalized_operational_name text GENERATED ALWAYS AS (lower(operational_name)) STORED NOT NULL;");
        migrationBuilder.CreateIndex(
            name: "UX_operational_configuration_contexts_normalized_name",
            schema: "operational_configuration",
            table: "contexts",
            column: "normalized_operational_name",
            unique: true);
        migrationBuilder.CreateTable(
            name: "context_creation_commands",
            schema: "operational_configuration",
            columns: table => new
            {
                idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                intent_operational_name = table.Column<string>(type: "text", nullable: false),
                result_context_id = table.Column<Guid>(type: "uuid", nullable: false),
                result_operational_name = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operational_configuration_context_creation_commands", x => x.idempotency_key);
                table.CheckConstraint("CK_operational_configuration_context_creation_command_names_not_empty", "length(btrim(intent_operational_name)) > 0 AND length(btrim(result_operational_name)) > 0");
                table.ForeignKey(
                    name: "FK_op_config_context_creation_command_context",
                    column: x => x.result_context_id,
                    principalSchema: "operational_configuration",
                    principalTable: "contexts",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex("UX_op_config_context_creation_command_result", "context_creation_commands", "result_context_id", schema: "operational_configuration", unique: true);

        // Upgrade-only import. Dynamic SQL is never parsed/executed on a fresh database.
        migrationBuilder.Sql(@"
DO $migration$
BEGIN
  IF to_regclass('order_operations.orders') IS NOT NULL
     AND to_regclass('order_operations.confirmation_history') IS NOT NULL
     AND to_regclass('order_operations.first_confirmation_commands') IS NOT NULL THEN
    EXECUTE $import$
      WITH candidates AS (
        SELECT btrim(context) AS display_name, 1 AS priority FROM order_operations.orders WHERE length(btrim(context)) > 0
        UNION ALL
        SELECT btrim(confirmed_context), 2 FROM order_operations.confirmation_history WHERE length(btrim(confirmed_context)) > 0
        UNION ALL
        SELECT btrim(intent_context), 3 FROM order_operations.first_confirmation_commands WHERE length(btrim(intent_context)) > 0
      ), chosen AS (
        SELECT DISTINCT ON (lower(display_name) COLLATE ""C"") lower(display_name) AS normalized, display_name
        FROM candidates
        ORDER BY lower(display_name) COLLATE ""C"", priority, display_name COLLATE ""C""
      ), numbered AS (
        SELECT normalized, display_name,
          row_number() OVER (ORDER BY normalized COLLATE ""C"") AS ordinal
        FROM chosen
      ), ids AS (
        SELECT normalized, display_name,
          (lpad(to_hex(floor(extract(epoch FROM transaction_timestamp()) * 1000)::bigint), 12, '0') ||
           '7' || substr(lpad(to_hex(ordinal), 18, '0'), 1, 3) ||
           '8' || substr(lpad(to_hex(ordinal), 18, '0'), 4, 15))::uuid AS id
        FROM numbered
      )
      INSERT INTO operational_configuration.contexts (id, operational_name)
      SELECT id, display_name FROM ids ORDER BY normalized COLLATE ""C"";
    $import$;
  END IF;
END
$migration$;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("context_creation_commands", "operational_configuration");
        migrationBuilder.DropTable("contexts", "operational_configuration");
    }
}
