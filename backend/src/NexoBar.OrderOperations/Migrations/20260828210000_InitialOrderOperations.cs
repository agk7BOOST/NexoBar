using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations;

[DbContext(typeof(OrderOperationsDbContext))]
[Migration("20260828210000_InitialOrderOperations")]
public partial class InitialOrderOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "order_operations");

        migrationBuilder.CreateTable(
            name: "orders",
            schema: "order_operations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                context = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_order_operations_orders", x => x.id);
                table.CheckConstraint(
                    "CK_order_operations_orders_context_not_empty",
                    "length(btrim(context)) > 0");
            });

        migrationBuilder.CreateTable(
            name: "incorporations",
            schema: "order_operations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                ordinal = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_order_operations_incorporations", x => x.id);
                table.CheckConstraint(
                    "CK_order_operations_incorporations_ordinal_positive",
                    "ordinal > 0");
                table.ForeignKey(
                    name: "FK_order_operations_incorporations_orders",
                    column: x => x.order_id,
                    principalSchema: "order_operations",
                    principalTable: "orders",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "confirmation_history",
            schema: "order_operations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                confirmed_context = table.Column<string>(type: "text", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_order_operations_confirmation_history", x => x.id);
                table.CheckConstraint(
                    "CK_order_operations_confirmation_history_context_not_empty",
                    "length(btrim(confirmed_context)) > 0");
                table.ForeignKey(
                    name: "FK_order_operations_confirmation_history_incorporations",
                    column: x => x.incorporation_id,
                    principalSchema: "order_operations",
                    principalTable: "incorporations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "first_confirmation_commands",
            schema: "order_operations",
            columns: table => new
            {
                idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                intent_context = table.Column<string>(type: "text", nullable: false),
                result_incorporation_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_order_operations_first_confirmation_commands",
                    x => x.idempotency_key);
                table.CheckConstraint(
                    "CK_order_operations_first_confirmation_commands_context_not_empty",
                    "length(btrim(intent_context)) > 0");
                table.ForeignKey(
                    name: "FK_order_operations_first_confirmation_commands_incorporations",
                    column: x => x.result_incorporation_id,
                    principalSchema: "order_operations",
                    principalTable: "incorporations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "incorporation_contents",
            schema: "order_operations",
            columns: table => new
            {
                incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                quantity = table.Column<int>(type: "integer", nullable: false),
                applied_price = table.Column<decimal>(type: "numeric", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_order_operations_incorporation_contents",
                    x => new { x.incorporation_id, x.product_id });
                table.CheckConstraint(
                    "CK_order_operations_incorporation_contents_price_non_negative",
                    "applied_price >= 0");
                table.CheckConstraint(
                    "CK_order_operations_incorporation_contents_quantity_positive",
                    "quantity > 0");
                table.ForeignKey(
                    name: "FK_order_operations_incorporation_contents_incorporations",
                    column: x => x.incorporation_id,
                    principalSchema: "order_operations",
                    principalTable: "incorporations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "first_confirmation_command_contents",
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
                    "PK_order_operations_first_confirmation_command_contents",
                    x => new { x.idempotency_key, x.product_id });
                table.CheckConstraint(
                    "CK_order_operations_first_confirmation_command_contents_quantity_positive",
                    "quantity > 0");
                table.ForeignKey(
                    name: "FK_order_operations_first_confirmation_command_contents_commands",
                    column: x => x.idempotency_key,
                    principalSchema: "order_operations",
                    principalTable: "first_confirmation_commands",
                    principalColumn: "idempotency_key",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_confirmation_history_incorporation",
            schema: "order_operations",
            table: "confirmation_history",
            column: "incorporation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_first_confirmation_commands_result_incorporation",
            schema: "order_operations",
            table: "first_confirmation_commands",
            column: "result_incorporation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_incorporations_order_ordinal",
            schema: "order_operations",
            table: "incorporations",
            columns: new[] { "order_id", "ordinal" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "confirmation_history",
            schema: "order_operations");
        migrationBuilder.DropTable(
            name: "first_confirmation_command_contents",
            schema: "order_operations");
        migrationBuilder.DropTable(
            name: "incorporation_contents",
            schema: "order_operations");
        migrationBuilder.DropTable(
            name: "first_confirmation_commands",
            schema: "order_operations");
        migrationBuilder.DropTable(
            name: "incorporations",
            schema: "order_operations");
        migrationBuilder.DropTable(
            name: "orders",
            schema: "order_operations");
    }
}
