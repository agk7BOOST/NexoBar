using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "delivery_states",
                schema: "order_operations",
                columns: table => new
                {
                    incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_ordinal = table.Column<int>(type: "integer", nullable: false),
                    delivered_quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_operations_delivery_states", x => new { x.incorporation_id, x.content_ordinal });
                    table.CheckConstraint("CK_order_operations_delivery_states_delivered_non_negative", "delivered_quantity >= 0");
                    table.ForeignKey(
                        name: "FK_order_operations_delivery_states_content",
                        columns: x => new { x.incorporation_id, x.content_ordinal },
                        principalSchema: "order_operations",
                        principalTable: "incorporation_contents",
                        principalColumns: new[] { "incorporation_id", "content_ordinal" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO order_operations.delivery_states
                    (incorporation_id, content_ordinal, delivered_quantity)
                SELECT incorporation_id, content_ordinal, 0
                FROM order_operations.incorporation_contents;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_states",
                schema: "order_operations");
        }
    }
}
