using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations
{
    /// <inheritdoc />
    public partial class AddLiquidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "liquidations",
                schema: "order_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    functional_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    declared_payment_medium = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_operations_liquidations", x => x.id);
                    table.CheckConstraint("CK_liquidations_amount_non_negative", "functional_amount >= 0");
                    table.CheckConstraint("CK_liquidations_medium_shape", "(mode = 'Simple' AND declared_payment_medium IS NOT NULL AND length(declared_payment_medium) BETWEEN 1 AND 200) OR (mode = 'ExternalCollection' AND declared_payment_medium IS NULL)");
                    table.CheckConstraint("CK_liquidations_mode", "mode IN ('Simple', 'ExternalCollection')");
                    table.ForeignKey(
                        name: "FK_liquidations_orders",
                        column: x => x.order_id,
                        principalSchema: "order_operations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "liquidation_commands",
                schema: "order_operations",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "text", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    declared_payment_medium = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    result_liquidation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_mode = table.Column<string>(type: "text", nullable: false),
                    result_functional_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    result_declared_payment_medium = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    result_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_operations_liquidation_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_liquidation_commands_intent_medium", "(command_kind = 'LiquidateSimple' AND declared_payment_medium IS NOT NULL AND length(declared_payment_medium) BETWEEN 1 AND 200) OR (command_kind = 'RecordExternalCollection' AND declared_payment_medium IS NULL)");
                    table.CheckConstraint("CK_liquidation_commands_kind", "command_kind IN ('LiquidateSimple', 'RecordExternalCollection')");
                    table.CheckConstraint("CK_liquidation_commands_result_amount_non_negative", "result_functional_amount >= 0");
                    table.ForeignKey(
                        name: "FK_liquidation_commands_liquidations",
                        column: x => x.result_liquidation_id,
                        principalSchema: "order_operations",
                        principalTable: "liquidations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_liquidation_commands_orders",
                        column: x => x.order_id,
                        principalSchema: "order_operations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "liquidation_history",
                schema: "order_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    liquidation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    functional_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    declared_payment_medium = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_operations_liquidation_history", x => x.id);
                    table.CheckConstraint("CK_liquidation_history_amount_non_negative", "functional_amount >= 0");
                    table.CheckConstraint("CK_liquidation_history_event_kind", "event_kind = 'Liquidated'");
                    table.CheckConstraint("CK_liquidation_history_medium_shape", "(mode = 'Simple' AND declared_payment_medium IS NOT NULL AND length(declared_payment_medium) BETWEEN 1 AND 200) OR (mode = 'ExternalCollection' AND declared_payment_medium IS NULL)");
                    table.CheckConstraint("CK_liquidation_history_mode", "mode IN ('Simple', 'ExternalCollection')");
                    table.ForeignKey(
                        name: "FK_liquidation_history_liquidations",
                        column: x => x.liquidation_id,
                        principalSchema: "order_operations",
                        principalTable: "liquidations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_liquidation_history_orders",
                        column: x => x.order_id,
                        principalSchema: "order_operations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_liquidation_commands_order",
                schema: "order_operations",
                table: "liquidation_commands",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "UX_liquidation_commands_liquidation",
                schema: "order_operations",
                table: "liquidation_commands",
                column: "result_liquidation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_liquidation_history_order_time_id",
                schema: "order_operations",
                table: "liquidation_history",
                columns: new[] { "order_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "UX_liquidation_history_liquidation",
                schema: "order_operations",
                table: "liquidation_history",
                column: "liquidation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_liquidations_order",
                schema: "order_operations",
                table: "liquidations",
                column: "order_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "liquidation_commands",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "liquidation_history",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "liquidations",
                schema: "order_operations");
        }
    }
}
