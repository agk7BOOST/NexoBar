using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryCountReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "count_observations",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    observed_movement_revision = table.Column<long>(type: "bigint", nullable: false),
                    observed_operational_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_count_observations", x => x.id);
                    table.CheckConstraint("CK_count_observations_quantity_non_negative", "observed_quantity >= 0");
                    table.CheckConstraint("CK_count_observations_revision_non_negative", "observed_movement_revision >= 0");
                    table.CheckConstraint("CK_count_observations_unit_valid", "length(observed_operational_unit) BETWEEN 1 AND 100 AND observed_operational_unit = btrim(observed_operational_unit) AND position(chr(10) in observed_operational_unit) = 0 AND position(chr(13) in observed_operational_unit) = 0");
                    table.ForeignKey(
                        name: "FK_count_observations_item",
                        column: x => x.inventory_item_id,
                        principalSchema: "inventory",
                        principalTable: "inventory_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "count_commands",
                schema: "inventory",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    inventory_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    result_count_observation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_observed_movement_revision = table.Column<long>(type: "bigint", nullable: false),
                    result_observed_operational_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    result_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_count_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_count_commands_kind", "command_kind = 'RecordInventoryCount'");
                    table.CheckConstraint("CK_count_commands_quantity_non_negative", "observed_quantity >= 0");
                    table.CheckConstraint("CK_count_commands_revision_non_negative", "result_observed_movement_revision >= 0");
                    table.ForeignKey(
                        name: "FK_count_commands_observation",
                        column: x => x.result_count_observation_id,
                        principalSchema: "inventory",
                        principalTable: "count_observations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_movements",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    movement_revision = table.Column<long>(type: "bigint", nullable: false),
                    nature = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    previous_registered_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    resulting_registered_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    count_observation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_movements", x => x.id);
                    table.CheckConstraint("CK_inventory_movements_nature", "nature IN ('Reconciliation', 'Entry', 'ManualExit', 'Waste', 'Correction')");
                    table.CheckConstraint("CK_inventory_movements_reconciliation_count", "nature <> 'Reconciliation' OR count_observation_id IS NOT NULL");
                    table.CheckConstraint("CK_inventory_movements_reconciliation_quantity", "nature <> 'Reconciliation' OR quantity >= 0");
                    table.CheckConstraint("CK_inventory_movements_revision_positive", "movement_revision > 0");
                    table.ForeignKey(
                        name: "FK_inventory_movements_count_observation",
                        column: x => x.count_observation_id,
                        principalSchema: "inventory",
                        principalTable: "count_observations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_movements_item",
                        column: x => x.inventory_item_id,
                        principalSchema: "inventory",
                        principalTable: "inventory_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "movement_commands",
                schema: "inventory",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    inventory_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    count_observation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    result_movement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_previous_registered_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
                    result_observed_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    result_resulting_registered_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
                    result_movement_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_movement_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_movement_commands_kind", "command_kind = 'ReconcileInventoryCount'");
                    table.CheckConstraint("CK_movement_commands_outcome", "result_outcome IN ('reconciled', 'no_discrepancy')");
                    table.CheckConstraint("CK_movement_commands_result_shape", "(result_outcome = 'reconciled' AND result_movement_id IS NOT NULL AND result_occurred_at IS NOT NULL) OR (result_outcome = 'no_discrepancy' AND result_movement_id IS NULL AND result_occurred_at IS NULL AND result_previous_registered_quantity = result_observed_quantity AND result_observed_quantity = result_resulting_registered_quantity)");
                    table.CheckConstraint("CK_movement_commands_revision_non_negative", "result_movement_revision >= 0");
                    table.ForeignKey(
                        name: "FK_movement_commands_item",
                        column: x => x.inventory_item_id,
                        principalSchema: "inventory",
                        principalTable: "inventory_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_movement_commands_movement",
                        column: x => x.result_movement_id,
                        principalSchema: "inventory",
                        principalTable: "inventory_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_movement_commands_observation",
                        column: x => x.count_observation_id,
                        principalSchema: "inventory",
                        principalTable: "count_observations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_count_commands_result_observation",
                schema: "inventory",
                table: "count_commands",
                column: "result_count_observation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_count_observations_item_observed_at",
                schema: "inventory",
                table: "count_observations",
                columns: new[] { "inventory_item_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "UX_inventory_movements_count_observation",
                schema: "inventory",
                table: "inventory_movements",
                column: "count_observation_id",
                unique: true,
                filter: "count_observation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_inventory_movements_item_revision",
                schema: "inventory",
                table: "inventory_movements",
                columns: new[] { "inventory_item_id", "movement_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_movement_commands_count_observation_id",
                schema: "inventory",
                table: "movement_commands",
                column: "count_observation_id");

            migrationBuilder.CreateIndex(
                name: "IX_movement_commands_inventory_item_id",
                schema: "inventory",
                table: "movement_commands",
                column: "inventory_item_id");

            migrationBuilder.CreateIndex(
                name: "UX_movement_commands_result_movement",
                schema: "inventory",
                table: "movement_commands",
                column: "result_movement_id",
                unique: true,
                filter: "result_movement_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "count_commands",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "movement_commands",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "inventory_movements",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "count_observations",
                schema: "inventory");
        }
    }
}
