using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddEverydayInventoryMovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_movement_commands_kind",
                schema: "inventory",
                table: "movement_commands");

            migrationBuilder.DropCheckConstraint(
                name: "CK_movement_commands_outcome",
                schema: "inventory",
                table: "movement_commands");

            migrationBuilder.DropCheckConstraint(
                name: "CK_movement_commands_result_shape",
                schema: "inventory",
                table: "movement_commands");

            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_movements_reconciliation_count",
                schema: "inventory",
                table: "inventory_movements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_movements_reconciliation_quantity",
                schema: "inventory",
                table: "inventory_movements");

            migrationBuilder.AlterColumn<decimal>(
                name: "result_observed_quantity",
                schema: "inventory",
                table: "movement_commands",
                type: "numeric(28,12)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(28,12)");

            migrationBuilder.AlterColumn<string>(
                name: "result_outcome",
                schema: "inventory",
                table: "movement_commands",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(24)",
                oldMaxLength: 24);

            migrationBuilder.AlterColumn<Guid>(
                name: "count_observation_id",
                schema: "inventory",
                table: "movement_commands",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<decimal>(
                name: "intent_quantity",
                schema: "inventory",
                table: "movement_commands",
                type: "numeric(28,12)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_movement_commands_kind",
                schema: "inventory",
                table: "movement_commands",
                sql: "command_kind IN ('ReconcileInventoryCount', 'RecordInventoryEntry', 'RecordManualInventoryExit', 'RecordInventoryWaste')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_movement_commands_outcome",
                schema: "inventory",
                table: "movement_commands",
                sql: "result_outcome IS NULL OR result_outcome IN ('reconciled', 'no_discrepancy')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_movement_commands_result_shape",
                schema: "inventory",
                table: "movement_commands",
                sql: "(command_kind = 'ReconcileInventoryCount' AND count_observation_id IS NOT NULL AND intent_quantity IS NULL AND result_observed_quantity IS NOT NULL AND ((result_outcome = 'reconciled' AND result_movement_id IS NOT NULL AND result_occurred_at IS NOT NULL) OR (result_outcome = 'no_discrepancy' AND result_movement_id IS NULL AND result_occurred_at IS NULL AND result_previous_registered_quantity = result_observed_quantity AND result_observed_quantity = result_resulting_registered_quantity))) OR (command_kind IN ('RecordInventoryEntry', 'RecordManualInventoryExit', 'RecordInventoryWaste') AND count_observation_id IS NULL AND intent_quantity > 0 AND result_outcome IS NULL AND result_observed_quantity IS NULL AND result_movement_id IS NOT NULL AND result_occurred_at IS NOT NULL AND result_previous_registered_quantity IS NOT NULL AND result_movement_revision > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_movements_reconciliation_count",
                schema: "inventory",
                table: "inventory_movements",
                sql: "(nature = 'Reconciliation' AND count_observation_id IS NOT NULL) OR (nature <> 'Reconciliation' AND count_observation_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_movements_reconciliation_quantity",
                schema: "inventory",
                table: "inventory_movements",
                sql: "(nature = 'Reconciliation' AND quantity >= 0) OR (nature IN ('Entry', 'ManualExit', 'Waste') AND quantity > 0) OR nature = 'Correction'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM inventory.movement_commands
                        WHERE command_kind <> 'ReconcileInventoryCount') THEN
                        RAISE EXCEPTION 'Cannot remove everyday Inventory Movement storage while commands exist.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_movement_commands_kind",
                schema: "inventory",
                table: "movement_commands");

            migrationBuilder.DropCheckConstraint(
                name: "CK_movement_commands_outcome",
                schema: "inventory",
                table: "movement_commands");

            migrationBuilder.DropCheckConstraint(
                name: "CK_movement_commands_result_shape",
                schema: "inventory",
                table: "movement_commands");

            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_movements_reconciliation_count",
                schema: "inventory",
                table: "inventory_movements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_movements_reconciliation_quantity",
                schema: "inventory",
                table: "inventory_movements");

            migrationBuilder.DropColumn(
                name: "intent_quantity",
                schema: "inventory",
                table: "movement_commands");

            migrationBuilder.AlterColumn<decimal>(
                name: "result_observed_quantity",
                schema: "inventory",
                table: "movement_commands",
                type: "numeric(28,12)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(28,12)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "result_outcome",
                schema: "inventory",
                table: "movement_commands",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(24)",
                oldMaxLength: 24,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "count_observation_id",
                schema: "inventory",
                table: "movement_commands",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_movement_commands_kind",
                schema: "inventory",
                table: "movement_commands",
                sql: "command_kind = 'ReconcileInventoryCount'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_movement_commands_outcome",
                schema: "inventory",
                table: "movement_commands",
                sql: "result_outcome IN ('reconciled', 'no_discrepancy')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_movement_commands_result_shape",
                schema: "inventory",
                table: "movement_commands",
                sql: "(result_outcome = 'reconciled' AND result_movement_id IS NOT NULL AND result_occurred_at IS NOT NULL) OR (result_outcome = 'no_discrepancy' AND result_movement_id IS NULL AND result_occurred_at IS NULL AND result_previous_registered_quantity = result_observed_quantity AND result_observed_quantity = result_resulting_registered_quantity)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_movements_reconciliation_count",
                schema: "inventory",
                table: "inventory_movements",
                sql: "nature <> 'Reconciliation' OR count_observation_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_movements_reconciliation_quantity",
                schema: "inventory",
                table: "inventory_movements",
                sql: "nature <> 'Reconciliation' OR quantity >= 0");
        }
    }
}
