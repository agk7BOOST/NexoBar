using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations;

public partial class AddPreparationWork : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "preparation_work",
            schema: "order_operations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                preparation_responsibility_id = table.Column<Guid>(
                    type: "uuid",
                    nullable: false),
                total_quantity = table.Column<int>(type: "integer", nullable: false),
                pending_quantity = table.Column<int>(type: "integer", nullable: false),
                in_preparation_quantity = table.Column<int>(
                    type: "integer",
                    nullable: false),
                ready_quantity = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_order_operations_preparation_work",
                    x => x.id);
                table.CheckConstraint(
                    "CK_order_operations_preparation_work_quantities_balanced",
                    "pending_quantity::bigint + in_preparation_quantity::bigint + " +
                    "ready_quantity::bigint = total_quantity::bigint");
                table.CheckConstraint(
                    "CK_order_operations_preparation_work_quantities_non_negative",
                    "pending_quantity >= 0 AND in_preparation_quantity >= 0 AND " +
                    "ready_quantity >= 0");
                table.CheckConstraint(
                    "CK_order_operations_preparation_work_total_positive",
                    "total_quantity > 0");
                table.ForeignKey(
                    name: "FK_order_operations_preparation_work_content",
                    columns: x => new { x.incorporation_id, x.product_id },
                    principalSchema: "order_operations",
                    principalTable: "incorporation_contents",
                    principalColumns: new[] { "incorporation_id", "product_id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_order_operations_preparation_work_responsibility",
            schema: "order_operations",
            table: "preparation_work",
            column: "preparation_responsibility_id");

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_preparation_work_content",
            schema: "order_operations",
            table: "preparation_work",
            columns: new[] { "incorporation_id", "product_id" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "preparation_work",
            schema: "order_operations");
    }
}
