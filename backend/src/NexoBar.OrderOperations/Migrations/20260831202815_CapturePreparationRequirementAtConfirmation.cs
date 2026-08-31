using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations
{
    /// <inheritdoc />
    public partial class CapturePreparationRequirementAtConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "requires_preparation_at_confirmation",
                schema: "order_operations",
                table: "incorporation_contents",
                type: "boolean",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE order_operations.incorporation_contents AS content
                SET requires_preparation_at_confirmation = EXISTS (
                    SELECT 1
                    FROM order_operations.preparation_work AS work
                    WHERE work.incorporation_id = content.incorporation_id
                      AND work.content_ordinal = content.content_ordinal
                );
                """);

            migrationBuilder.AlterColumn<bool>(
                name: "requires_preparation_at_confirmation",
                schema: "order_operations",
                table: "incorporation_contents",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "requires_preparation_at_confirmation",
                schema: "order_operations",
                table: "incorporation_contents");
        }
    }
}
