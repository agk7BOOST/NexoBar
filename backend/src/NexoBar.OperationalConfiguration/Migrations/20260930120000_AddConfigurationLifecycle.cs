using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OperationalConfiguration.Migrations;

[DbContext(typeof(OperationalConfigurationDbContext))]
[Migration("20260930120000_AddConfigurationLifecycle")]
public partial class AddConfigurationLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "is_active", table: "contexts", type: "boolean", schema: "operational_configuration", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<bool>(name: "is_active", table: "preparation_responsibilities", type: "boolean", schema: "operational_configuration", nullable: false, defaultValue: true);
        migrationBuilder.DropForeignKey("FK_op_config_context_creation_command_context", "context_creation_commands", "operational_configuration");
        migrationBuilder.DropForeignKey("FK_op_config_prep_responsibility_creation_cmd_responsibility", "preparation_responsibility_creation_commands", "operational_configuration");
        foreach (var name in new[] { "context_lifecycle_commands", "preparation_responsibility_lifecycle_commands" })
            migrationBuilder.CreateTable(name: name, schema: "operational_configuration", columns: table => new
            {
                idempotency_key = table.Column<Guid>("uuid", nullable: false),
                actor_identity_id = table.Column<Guid>("uuid", nullable: false),
                command_kind = table.Column<string>("text", nullable: false),
                target_id = table.Column<Guid>("uuid", nullable: false),
                expected_operational_name = table.Column<string>("text", nullable: false),
                expected_is_active = table.Column<bool>("boolean", nullable: false),
                new_operational_name = table.Column<string>("text", nullable: true),
                result_operational_name = table.Column<string>("text", nullable: false),
                result_is_active = table.Column<bool>("boolean", nullable: false),
                result_is_deleted = table.Column<bool>("boolean", nullable: false)
            }, constraints: table =>
            {
                table.PrimaryKey($"PK_{name}", x => x.idempotency_key);
                table.CheckConstraint($"CK_{name}_kind", "command_kind IN ('Rename', 'Retire', 'Reactivate', 'Delete')");
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Refuse to erase durable commands or lifecycle State to force a rollback.
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM operational_configuration.context_lifecycle_commands)
                   OR EXISTS (SELECT 1 FROM operational_configuration.preparation_responsibility_lifecycle_commands)
                   OR EXISTS (SELECT 1 FROM operational_configuration.contexts WHERE NOT is_active)
                   OR EXISTS (SELECT 1 FROM operational_configuration.preparation_responsibilities WHERE NOT is_active)
                   OR EXISTS (SELECT 1 FROM operational_configuration.context_creation_commands c WHERE NOT EXISTS (SELECT 1 FROM operational_configuration.contexts s WHERE s.id = c.result_context_id))
                   OR EXISTS (SELECT 1 FROM operational_configuration.preparation_responsibility_creation_commands c WHERE NOT EXISTS (SELECT 1 FROM operational_configuration.preparation_responsibilities s WHERE s.id = c.result_responsibility_id))
                THEN RAISE EXCEPTION 'Configuration lifecycle State or durable results cannot be discarded'; END IF;
            END $$;
            """);
        migrationBuilder.DropTable("context_lifecycle_commands", "operational_configuration");
        migrationBuilder.DropTable("preparation_responsibility_lifecycle_commands", "operational_configuration");
        migrationBuilder.DropColumn("is_active", "contexts", "operational_configuration");
        migrationBuilder.DropColumn("is_active", "preparation_responsibilities", "operational_configuration");
        migrationBuilder.AddForeignKey("FK_op_config_context_creation_command_context", "context_creation_commands", "result_context_id", "contexts", schema: "operational_configuration", principalSchema: "operational_configuration", principalColumn: "id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey("FK_op_config_prep_responsibility_creation_cmd_responsibility", "preparation_responsibility_creation_commands", "result_responsibility_id", "preparation_responsibilities", schema: "operational_configuration", principalSchema: "operational_configuration", principalColumn: "id", onDelete: ReferentialAction.Restrict);
    }
}
