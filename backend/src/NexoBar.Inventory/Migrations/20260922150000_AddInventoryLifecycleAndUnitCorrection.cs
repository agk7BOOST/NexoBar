using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.Inventory.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(InventoryDbContext))]
    [Migration("20260922150000_AddInventoryLifecycleAndUnitCorrection")]
    public partial class AddInventoryLifecycleAndUnitCorrection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "inventory",
                table: "inventory_items",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_items_retired_quantity_null",
                schema: "inventory",
                table: "inventory_items",
                sql: "is_active OR current_registered_quantity IS NULL");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "invalidated_at_utc",
                schema: "inventory",
                table: "count_observations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.DropIndex(
                name: "UX_inventory_items_normalized_name",
                schema: "inventory",
                table: "inventory_items");

            migrationBuilder.CreateIndex(
                name: "UX_inventory_items_active_normalized_name",
                schema: "inventory",
                table: "inventory_items",
                column: "normalized_operational_name",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateTable(
                name: "retire_commands",
                schema: "inventory",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    intent_expected_current_is_active = table.Column<bool>(type: "boolean", nullable: false),
                    result_operational_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    result_operational_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    result_is_active = table.Column<bool>(type: "boolean", nullable: false),
                    result_movement_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_retire_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_inventory_retire_commands_kind", "command_kind = 'RetireInventoryItem'");
                    table.CheckConstraint("CK_inventory_retire_commands_result", "result_is_active = false");
                });

            migrationBuilder.CreateTable(
                name: "reactivate_commands",
                schema: "inventory",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    intent_expected_current_is_active = table.Column<bool>(type: "boolean", nullable: false),
                    intent_new_operational_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    result_operational_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    result_operational_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    result_is_active = table.Column<bool>(type: "boolean", nullable: false),
                    result_movement_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_reactivate_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_inventory_reactivate_commands_kind", "command_kind = 'ReactivateInventoryItem'");
                    table.CheckConstraint("CK_inventory_reactivate_commands_result", "result_is_active = true");
                });

            migrationBuilder.CreateTable(
                name: "unit_correction_commands",
                schema: "inventory",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    intent_expected_current_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    intent_new_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    result_operational_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    result_outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_unit_correction_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_inventory_unit_correction_commands_kind", "command_kind = 'CorrectInventoryUnit'");
                    table.CheckConstraint("CK_inventory_unit_correction_commands_outcome", "result_outcome IN ('corrected', 'no_change')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "retire_commands",
                schema: "inventory");
            migrationBuilder.DropTable(
                name: "reactivate_commands",
                schema: "inventory");
            migrationBuilder.DropTable(
                name: "unit_correction_commands",
                schema: "inventory");

            migrationBuilder.DropIndex(
                name: "UX_inventory_items_active_normalized_name",
                schema: "inventory",
                table: "inventory_items");

            migrationBuilder.CreateIndex(
                name: "UX_inventory_items_normalized_name",
                schema: "inventory",
                table: "inventory_items",
                column: "normalized_operational_name",
                unique: true);

            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_items_retired_quantity_null",
                schema: "inventory",
                table: "inventory_items");
            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "inventory",
                table: "inventory_items");
            migrationBuilder.DropColumn(
                name: "invalidated_at_utc",
                schema: "inventory",
                table: "count_observations");
        }
    }
}
