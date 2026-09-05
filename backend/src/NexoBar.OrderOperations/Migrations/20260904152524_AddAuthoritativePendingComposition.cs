using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthoritativePendingComposition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "actor_identity_id",
                schema: "order_operations",
                table: "subsequent_confirmation_commands",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "intent_pending_composition_id",
                schema: "order_operations",
                table: "subsequent_confirmation_commands",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "actor_identity_id",
                schema: "order_operations",
                table: "first_confirmation_commands",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "actor_identity_id",
                schema: "order_operations",
                table: "confirmation_history",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pending_composition_commands",
                schema: "order_operations",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "text", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_pending_composition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result_pending_composition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result_created_by_identity_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_composition_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_pending_composition_commands_intent", "(command_kind = 'StartPendingComposition' AND intent_pending_composition_id IS NULL) OR (command_kind = 'DiscardPendingComposition' AND intent_pending_composition_id IS NOT NULL)");
                    table.CheckConstraint("CK_pending_composition_commands_kind", "command_kind IN ('StartPendingComposition', 'DiscardPendingComposition')");
                    table.ForeignKey(
                        name: "FK_pending_composition_commands_orders",
                        column: x => x.order_id,
                        principalSchema: "order_operations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pending_compositions",
                schema: "order_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_identity_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_operations_pending_compositions", x => x.id);
                    table.ForeignKey(
                        name: "FK_pending_compositions_orders",
                        column: x => x.order_id,
                        principalSchema: "order_operations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_composition_commands_order",
                schema: "order_operations",
                table: "pending_composition_commands",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "UX_pending_compositions_order",
                schema: "order_operations",
                table: "pending_compositions",
                column: "order_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_composition_commands",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "pending_compositions",
                schema: "order_operations");

            migrationBuilder.DropColumn(
                name: "actor_identity_id",
                schema: "order_operations",
                table: "subsequent_confirmation_commands");

            migrationBuilder.DropColumn(
                name: "intent_pending_composition_id",
                schema: "order_operations",
                table: "subsequent_confirmation_commands");

            migrationBuilder.DropColumn(
                name: "actor_identity_id",
                schema: "order_operations",
                table: "first_confirmation_commands");

            migrationBuilder.DropColumn(
                name: "actor_identity_id",
                schema: "order_operations",
                table: "confirmation_history");
        }
    }
}
