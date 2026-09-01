using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "delivery_history",
                schema: "order_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_ordinal = table.Column<int>(type: "integer", nullable: false),
                    event_kind = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resulting_delivered_quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_operations_delivery_history", x => x.id);
                    table.CheckConstraint("CK_delivery_history_content_ordinal_positive", "content_ordinal > 0");
                    table.CheckConstraint("CK_delivery_history_event_kind_not_empty", "length(btrim(event_kind)) > 0");
                    table.CheckConstraint("CK_delivery_history_quantity_positive", "quantity > 0");
                    table.CheckConstraint("CK_delivery_history_result_non_negative", "resulting_delivered_quantity >= 0");
                    table.ForeignKey(
                        name: "FK_delivery_history_content",
                        columns: x => new { x.incorporation_id, x.content_ordinal },
                        principalSchema: "order_operations",
                        principalTable: "incorporation_contents",
                        principalColumns: new[] { "incorporation_id", "content_ordinal" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delivery_commands",
                schema: "order_operations",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "text", nullable: false),
                    incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_ordinal = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    result_history_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result_delivered_quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_operations_delivery_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_delivery_commands_content_ordinal_positive", "content_ordinal > 0");
                    table.CheckConstraint("CK_delivery_commands_kind_not_empty", "length(btrim(command_kind)) > 0");
                    table.CheckConstraint("CK_delivery_commands_quantity_positive", "quantity > 0");
                    table.CheckConstraint("CK_delivery_commands_result_non_negative", "result_delivered_quantity >= 0");
                    table.ForeignKey(
                        name: "FK_delivery_commands_content",
                        columns: x => new { x.incorporation_id, x.content_ordinal },
                        principalSchema: "order_operations",
                        principalTable: "incorporation_contents",
                        principalColumns: new[] { "incorporation_id", "content_ordinal" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_commands_history",
                        column: x => x.result_history_id,
                        principalSchema: "order_operations",
                        principalTable: "delivery_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_commands_content",
                schema: "order_operations",
                table: "delivery_commands",
                columns: new[] { "incorporation_id", "content_ordinal" });

            migrationBuilder.CreateIndex(
                name: "UX_delivery_commands_history",
                schema: "order_operations",
                table: "delivery_commands",
                column: "result_history_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_history_content_time_id",
                schema: "order_operations",
                table: "delivery_history",
                columns: new[] { "incorporation_id", "content_ordinal", "occurred_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_commands",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "delivery_history",
                schema: "order_operations");
        }
    }
}
