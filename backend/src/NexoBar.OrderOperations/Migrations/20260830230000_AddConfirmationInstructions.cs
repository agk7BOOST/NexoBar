using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations;

public partial class AddConfirmationInstructions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_order_operations_incorporation_contents_product",
            schema: "order_operations",
            table: "incorporation_contents");

        migrationBuilder.DropIndex(
            name: "UX_order_operations_first_command_contents_product",
            schema: "order_operations",
            table: "first_confirmation_command_contents");

        migrationBuilder.DropIndex(
            name: "UX_order_operations_subsequent_command_contents_product",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents");

        migrationBuilder.AddColumn<string>(
            name: "instruction",
            schema: "order_operations",
            table: "incorporation_contents",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "instruction",
            schema: "order_operations",
            table: "first_confirmation_command_contents",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "instruction",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $rollback$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM order_operations.incorporation_contents
                    GROUP BY incorporation_id, product_id
                    HAVING COUNT(*) > 1
                ) OR EXISTS (
                    SELECT 1
                    FROM order_operations.first_confirmation_command_contents
                    GROUP BY idempotency_key, product_id
                    HAVING COUNT(*) > 1
                ) OR EXISTS (
                    SELECT 1
                    FROM order_operations.subsequent_confirmation_command_contents
                    GROUP BY idempotency_key, product_id
                    HAVING COUNT(*) > 1
                ) THEN
                    RAISE EXCEPTION
                        'Cannot roll back AddConfirmationInstructions: duplicate Product lines require the new schema.';
                END IF;
            END
            $rollback$;
            """);

        migrationBuilder.DropColumn(
            name: "instruction",
            schema: "order_operations",
            table: "incorporation_contents");

        migrationBuilder.DropColumn(
            name: "instruction",
            schema: "order_operations",
            table: "first_confirmation_command_contents");

        migrationBuilder.DropColumn(
            name: "instruction",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents");

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_incorporation_contents_product",
            schema: "order_operations",
            table: "incorporation_contents",
            columns: new[] { "incorporation_id", "product_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_first_command_contents_product",
            schema: "order_operations",
            table: "first_confirmation_command_contents",
            columns: new[] { "idempotency_key", "product_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_subsequent_command_contents_product",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents",
            columns: new[] { "idempotency_key", "product_id" },
            unique: true);
    }
}
