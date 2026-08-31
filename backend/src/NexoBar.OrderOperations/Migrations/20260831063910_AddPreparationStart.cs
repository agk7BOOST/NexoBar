using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations
{
    /// <inheritdoc />
    public partial class AddPreparationStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "preparation_history",
                schema: "order_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resulting_total_quantity = table.Column<int>(type: "integer", nullable: false),
                    resulting_pending_quantity = table.Column<int>(type: "integer", nullable: false),
                    resulting_in_preparation_quantity = table.Column<int>(type: "integer", nullable: false),
                    resulting_ready_quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_operations_preparation_history", x => x.id);
                    table.CheckConstraint("CK_preparation_history_event_kind_not_empty", "length(btrim(event_kind)) > 0");
                    table.CheckConstraint("CK_preparation_history_quantity_positive", "quantity > 0");
                    table.CheckConstraint("CK_preparation_history_result_balanced", "resulting_pending_quantity::bigint + resulting_in_preparation_quantity::bigint + resulting_ready_quantity::bigint = resulting_total_quantity::bigint");
                    table.CheckConstraint("CK_preparation_history_result_non_negative", "resulting_pending_quantity >= 0 AND resulting_in_preparation_quantity >= 0 AND resulting_ready_quantity >= 0");
                    table.CheckConstraint("CK_preparation_history_result_total_positive", "resulting_total_quantity > 0");
                    table.ForeignKey(
                        name: "FK_order_operations_preparation_history_work",
                        column: x => x.work_id,
                        principalSchema: "order_operations",
                        principalTable: "preparation_work",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "preparation_commands",
                schema: "order_operations",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "text", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    result_history_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result_total_quantity = table.Column<int>(type: "integer", nullable: false),
                    result_pending_quantity = table.Column<int>(type: "integer", nullable: false),
                    result_in_preparation_quantity = table.Column<int>(type: "integer", nullable: false),
                    result_ready_quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_operations_preparation_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_preparation_commands_kind_not_empty", "length(btrim(command_kind)) > 0");
                    table.CheckConstraint("CK_preparation_commands_quantity_positive", "quantity > 0");
                    table.CheckConstraint("CK_preparation_commands_result_balanced", "result_pending_quantity::bigint + result_in_preparation_quantity::bigint + result_ready_quantity::bigint = result_total_quantity::bigint");
                    table.CheckConstraint("CK_preparation_commands_result_non_negative", "result_pending_quantity >= 0 AND result_in_preparation_quantity >= 0 AND result_ready_quantity >= 0");
                    table.CheckConstraint("CK_preparation_commands_result_total_positive", "result_total_quantity > 0");
                    table.ForeignKey(
                        name: "FK_order_operations_preparation_commands_history",
                        column: x => x.result_history_id,
                        principalSchema: "order_operations",
                        principalTable: "preparation_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_operations_preparation_commands_work",
                        column: x => x.work_id,
                        principalSchema: "order_operations",
                        principalTable: "preparation_work",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_operations_preparation_commands_work",
                schema: "order_operations",
                table: "preparation_commands",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "UX_order_operations_preparation_commands_history",
                schema: "order_operations",
                table: "preparation_commands",
                column: "result_history_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_operations_preparation_history_work_time_id",
                schema: "order_operations",
                table: "preparation_history",
                columns: new[] { "work_id", "occurred_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "preparation_commands",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "preparation_history",
                schema: "order_operations");
        }
    }
}
