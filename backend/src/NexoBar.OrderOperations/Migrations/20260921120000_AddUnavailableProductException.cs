using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations
{
    public partial class AddUnavailableProductException : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "unavailable_product_exception_applied",
                schema: "order_operations",
                table: "incorporation_contents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "intent_unavailable_product_exception_requested",
                schema: "order_operations",
                table: "first_confirmation_command_contents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "intent_unavailable_product_exception_requested",
                schema: "order_operations",
                table: "subsequent_confirmation_command_contents",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM order_operations.incorporation_contents WHERE unavailable_product_exception_applied)
                       OR EXISTS (SELECT 1 FROM order_operations.first_confirmation_command_contents WHERE intent_unavailable_product_exception_requested)
                       OR EXISTS (SELECT 1 FROM order_operations.subsequent_confirmation_command_contents WHERE intent_unavailable_product_exception_requested) THEN
                        RAISE EXCEPTION 'Cannot remove meaningful unavailable Product exception State or command intent';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "unavailable_product_exception_applied",
                schema: "order_operations",
                table: "incorporation_contents");

            migrationBuilder.DropColumn(
                name: "intent_unavailable_product_exception_requested",
                schema: "order_operations",
                table: "first_confirmation_command_contents");

            migrationBuilder.DropColumn(
                name: "intent_unavailable_product_exception_requested",
                schema: "order_operations",
                table: "subsequent_confirmation_command_contents");
        }
    }
}
