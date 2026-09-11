using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations
{
    /// <inheritdoc />
    public partial class AddCompleteOrderCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "complete_cancellation_history",
                schema: "order_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pending_composition_discarded = table.Column<bool>(type: "boolean", nullable: false),
                    event_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_complete_cancellation_history", x => x.id);
                    table.UniqueConstraint("AK_complete_cancellation_history_order_id_id", x => new { x.order_id, x.id });
                    table.CheckConstraint("CK_complete_cancellation_history_kind", "event_kind = 'OrderCompletelyCancelled'");
                    table.ForeignKey(
                        name: "FK_complete_cancellation_history_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "order_operations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "complete_cancellation_commands",
                schema: "order_operations",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    cancellation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_complete_cancellation_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_complete_cancellation_commands_kind", "command_kind = 'CompleteOrderCancellation'");
                    table.ForeignKey(
                        name: "FK_complete_cancellation_commands_complete_cancellation_histor~",
                        column: x => x.cancellation_id,
                        principalSchema: "order_operations",
                        principalTable: "complete_cancellation_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "complete_cancellation_details",
                schema: "order_operations",
                columns: table => new
                {
                    cancellation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_ordinal = table.Column<int>(type: "integer", nullable: false),
                    direct_or_pending_quantity = table.Column<int>(type: "integer", nullable: false),
                    in_preparation_quantity = table.Column<int>(type: "integer", nullable: false),
                    ready_quantity = table.Column<int>(type: "integer", nullable: false),
                    resulting_fulfillment_quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_complete_cancellation_details", x => new { x.cancellation_id, x.incorporation_id, x.content_ordinal });
                    table.CheckConstraint("CK_complete_cancellation_details_quantities", "direct_or_pending_quantity >= 0 AND in_preparation_quantity >= 0 AND ready_quantity >= 0 AND direct_or_pending_quantity::bigint + in_preparation_quantity + ready_quantity > 0 AND resulting_fulfillment_quantity = 0");
                    table.ForeignKey(
                        name: "FK_complete_cancellation_details_complete_cancellation_history~",
                        column: x => x.cancellation_id,
                        principalSchema: "order_operations",
                        principalTable: "complete_cancellation_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_complete_cancellation_details_incorporation_contents_incorp~",
                        columns: x => new { x.incorporation_id, x.content_ordinal },
                        principalSchema: "order_operations",
                        principalTable: "incorporation_contents",
                        principalColumns: new[] { "incorporation_id", "content_ordinal" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_cancellation_states",
                schema: "order_operations",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cancellation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_cancellation_states", x => x.order_id);
                    table.ForeignKey(
                        name: "FK_order_cancellation_states_complete_cancellation_history_ord~",
                        columns: x => new { x.order_id, x.cancellation_id },
                        principalSchema: "order_operations",
                        principalTable: "complete_cancellation_history",
                        principalColumns: new[] { "order_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_cancellation_states_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "order_operations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_complete_cancellation_commands_cancellation_id",
                schema: "order_operations",
                table: "complete_cancellation_commands",
                column: "cancellation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_complete_cancellation_details_incorporation_id_content_ordi~",
                schema: "order_operations",
                table: "complete_cancellation_details",
                columns: new[] { "incorporation_id", "content_ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_complete_cancellation_history_order_id",
                schema: "order_operations",
                table: "complete_cancellation_history",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_cancellation_states_order_id_cancellation_id",
                schema: "order_operations",
                table: "order_cancellation_states",
                columns: new[] { "order_id", "cancellation_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM order_operations.complete_cancellation_history)
                       OR EXISTS (SELECT 1 FROM order_operations.order_cancellation_states)
                       OR EXISTS (SELECT 1 FROM order_operations.complete_cancellation_details)
                       OR EXISTS (SELECT 1 FROM order_operations.complete_cancellation_commands) THEN
                        RAISE EXCEPTION 'Cannot remove meaningful Complete Cancellation State or History';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "complete_cancellation_commands",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "complete_cancellation_details",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "order_cancellation_states",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "complete_cancellation_history",
                schema: "order_operations");
        }
    }
}
