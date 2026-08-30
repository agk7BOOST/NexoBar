using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations;

public partial class AddSubsequentConfirmationCommands : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "subsequent_confirmation_commands",
            schema: "order_operations",
            columns: table => new
            {
                idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                intent_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                result_incorporation_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_order_operations_subsequent_confirmation_commands",
                    x => x.idempotency_key);
                table.ForeignKey(
                    name: "FK_order_operations_subsequent_confirmation_commands_incorporations",
                    column: x => x.result_incorporation_id,
                    principalSchema: "order_operations",
                    principalTable: "incorporations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_order_operations_subsequent_confirmation_commands_orders",
                    column: x => x.intent_order_id,
                    principalSchema: "order_operations",
                    principalTable: "orders",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "subsequent_confirmation_command_contents",
            schema: "order_operations",
            columns: table => new
            {
                idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                quantity = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_order_operations_subsequent_confirmation_command_contents",
                    x => new { x.idempotency_key, x.product_id });
                table.CheckConstraint(
                    "CK_order_operations_subsequent_confirmation_command_contents_quantity_positive",
                    "quantity > 0");
                table.ForeignKey(
                    name: "FK_order_operations_subsequent_confirmation_command_contents_commands",
                    column: x => x.idempotency_key,
                    principalSchema: "order_operations",
                    principalTable: "subsequent_confirmation_commands",
                    principalColumn: "idempotency_key",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_order_operations_subsequent_confirmation_commands_intent_order",
            schema: "order_operations",
            table: "subsequent_confirmation_commands",
            column: "intent_order_id");

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_subsequent_confirmation_commands_result_incorporation",
            schema: "order_operations",
            table: "subsequent_confirmation_commands",
            column: "result_incorporation_id",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "subsequent_confirmation_command_contents",
            schema: "order_operations");

        migrationBuilder.DropTable(
            name: "subsequent_confirmation_commands",
            schema: "order_operations");
    }
}
