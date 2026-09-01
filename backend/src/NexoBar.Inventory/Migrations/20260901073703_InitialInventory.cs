using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "inventory_items",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operational_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    normalized_operational_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    operational_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    current_registered_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    movement_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_items", x => x.id);
                    table.CheckConstraint("CK_inventory_items_name_valid", "length(operational_name) BETWEEN 1 AND 200 AND operational_name = btrim(operational_name) AND position(chr(10) in operational_name) = 0 AND position(chr(13) in operational_name) = 0");
                    table.CheckConstraint("CK_inventory_items_revision_non_negative", "movement_revision >= 0");
                    table.CheckConstraint("CK_inventory_items_unit_valid", "length(operational_unit) BETWEEN 1 AND 100 AND operational_unit = btrim(operational_unit) AND position(chr(10) in operational_unit) = 0 AND position(chr(13) in operational_unit) = 0");
                });

            migrationBuilder.CreateTable(
                name: "item_creation_commands",
                schema: "inventory",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    intent_normalized_operational_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    intent_operational_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    result_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_operational_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    result_operational_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    result_current_registered_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    result_movement_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_item_creation_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_inventory_item_commands_kind", "command_kind = 'CreateInventoryItem'");
                    table.CheckConstraint("CK_inventory_item_commands_revision", "result_movement_revision >= 0");
                    table.ForeignKey(
                        name: "FK_inventory_item_commands_item",
                        column: x => x.result_item_id,
                        principalSchema: "inventory",
                        principalTable: "inventory_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_inventory_items_normalized_name",
                schema: "inventory",
                table: "inventory_items",
                column: "normalized_operational_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_inventory_item_commands_result_item",
                schema: "inventory",
                table: "item_creation_commands",
                column: "result_item_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_creation_commands",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "inventory_items",
                schema: "inventory");
        }
    }
}
