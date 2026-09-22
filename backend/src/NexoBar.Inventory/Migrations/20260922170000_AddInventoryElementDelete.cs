using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.Inventory.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(InventoryDbContext))]
    [Migration("20260922170000_AddInventoryElementDelete")]
    public partial class AddInventoryElementDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_inventory_item_commands_item",
                schema: "inventory",
                table: "item_creation_commands");

            migrationBuilder.DropForeignKey(
                name: "FK_count_commands_observation",
                schema: "inventory",
                table: "count_commands");

            migrationBuilder.DropForeignKey(
                name: "FK_movement_commands_observation",
                schema: "inventory",
                table: "movement_commands");

            migrationBuilder.DropForeignKey(
                name: "FK_movement_commands_item",
                schema: "inventory",
                table: "movement_commands");

            migrationBuilder.CreateTable(
                name: "delete_commands",
                schema: "inventory",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    result_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    committed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_delete_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_inventory_delete_commands_kind", "command_kind = 'DeleteInventoryItem'");
                    table.CheckConstraint("CK_inventory_delete_commands_result", "result_deleted = true");
                });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_delete_commands_item",
                schema: "inventory",
                table: "delete_commands",
                column: "inventory_item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delete_commands",
                schema: "inventory");

            migrationBuilder.AddForeignKey(
                name: "FK_inventory_item_commands_item",
                schema: "inventory",
                table: "item_creation_commands",
                column: "result_item_id",
                principalSchema: "inventory",
                principalTable: "inventory_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_count_commands_observation",
                schema: "inventory",
                table: "count_commands",
                column: "result_count_observation_id",
                principalSchema: "inventory",
                principalTable: "count_observations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_movement_commands_observation",
                schema: "inventory",
                table: "movement_commands",
                column: "count_observation_id",
                principalSchema: "inventory",
                principalTable: "count_observations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_movement_commands_item",
                schema: "inventory",
                table: "movement_commands",
                column: "inventory_item_id",
                principalSchema: "inventory",
                principalTable: "inventory_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
