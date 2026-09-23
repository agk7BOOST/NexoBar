using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoBar.OrderOperations.Migrations;

[DbContext(typeof(OrderOperationsDbContext))]
[Migration("20260922160000_AddProductOperationalNameSnapshot")]
public partial class AddProductOperationalNameSnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "product_operational_name_snapshot",
            schema: "order_operations",
            table: "incorporation_contents",
            type: "text",
            nullable: true);

        migrationBuilder.Sql(
            """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM order_operations.incorporation_contents AS content
                    LEFT JOIN catalog.product_creation_commands AS creation
                      ON creation.result_product_id = content.product_id
                     AND creation.command_kind = 'CreateProduct'
                    GROUP BY content.product_id
                HAVING count(DISTINCT creation.idempotency_key) <> 1
                        OR bool_or(creation.intent_operational_name IS NULL)
                        OR bool_or(length(btrim(creation.intent_operational_name)) = 0)
                ) THEN
                    RAISE EXCEPTION
                        'Cannot backfill Product name snapshots: a confirmed Product lacks one trustworthy durable creation command.';
                END IF;
            END $$;
            """);

        migrationBuilder.Sql(
            """
            UPDATE order_operations.incorporation_contents AS content
            SET product_operational_name_snapshot = CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM catalog.product_operational_name_change_commands AS rename
                    WHERE rename.product_id = content.product_id
                      AND rename.command_kind = 'ChangeProductOperationalName'
                ) THEN NULL
                ELSE creation.intent_operational_name
            END
            FROM catalog.product_creation_commands AS creation
            WHERE creation.result_product_id = content.product_id
              AND creation.command_kind = 'CreateProduct';
            """);

        migrationBuilder.AddCheckConstraint(
            name: "CK_incorporation_contents_product_name_snapshot_not_blank",
            schema: "order_operations",
            table: "incorporation_contents",
            sql: "product_operational_name_snapshot IS NULL OR " +
                "length(btrim(product_operational_name_snapshot)) > 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_incorporation_contents_product_name_snapshot_not_blank",
            schema: "order_operations",
            table: "incorporation_contents");
        migrationBuilder.DropColumn(
            name: "product_operational_name_snapshot",
            schema: "order_operations",
            table: "incorporation_contents");
    }
}
