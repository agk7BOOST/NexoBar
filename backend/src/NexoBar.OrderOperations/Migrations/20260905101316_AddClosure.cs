using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations
{
    /// <inheritdoc />
    public partial class AddClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "closures",
                schema: "order_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_closures", x => x.id);
                    table.ForeignKey(
                        name: "FK_closures_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "order_operations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "closure_commands",
                schema: "order_operations",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_closure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_closure_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_closure_commands_kind", "command_kind = 'CloseOrder'");
                    table.ForeignKey(
                        name: "FK_closure_commands_closures_result_closure_id",
                        column: x => x.result_closure_id,
                        principalSchema: "order_operations",
                        principalTable: "closures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_closure_commands_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "order_operations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "closure_history",
                schema: "order_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    closure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_closure_history", x => x.id);
                    table.CheckConstraint("CK_closure_history_event_kind", "event_kind = 'Closed'");
                    table.ForeignKey(
                        name: "FK_closure_history_closures_closure_id",
                        column: x => x.closure_id,
                        principalSchema: "order_operations",
                        principalTable: "closures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_closure_history_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "order_operations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_closure_commands_order_id",
                schema: "order_operations",
                table: "closure_commands",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_closure_commands_result_closure_id",
                schema: "order_operations",
                table: "closure_commands",
                column: "result_closure_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_closure_history_closure_id",
                schema: "order_operations",
                table: "closure_history",
                column: "closure_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_closure_history_order_id_occurred_at_id",
                schema: "order_operations",
                table: "closure_history",
                columns: new[] { "order_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_closures_order_id",
                schema: "order_operations",
                table: "closures",
                column: "order_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "closure_commands",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "closure_history",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "closures",
                schema: "order_operations");
        }
    }
}
