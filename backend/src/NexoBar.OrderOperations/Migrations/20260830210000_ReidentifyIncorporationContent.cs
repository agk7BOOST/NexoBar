using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations;

public partial class ReidentifyIncorporationContent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "content_ordinal",
            schema: "order_operations",
            table: "incorporation_contents",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "content_ordinal",
            schema: "order_operations",
            table: "preparation_work",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "line_ordinal",
            schema: "order_operations",
            table: "first_confirmation_command_contents",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "line_ordinal",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents",
            type: "integer",
            nullable: true);

        migrationBuilder.Sql(
            """
            WITH ranked AS (
                SELECT incorporation_id,
                       product_id,
                       (ROW_NUMBER() OVER (
                           PARTITION BY incorporation_id
                           ORDER BY product_id))::integer AS content_ordinal
                FROM order_operations.incorporation_contents
            )
            UPDATE order_operations.incorporation_contents AS content
            SET content_ordinal = ranked.content_ordinal
            FROM ranked
            WHERE content.incorporation_id = ranked.incorporation_id
              AND content.product_id = ranked.product_id;
            """);

        migrationBuilder.Sql(
            """
            UPDATE order_operations.preparation_work AS work
            SET content_ordinal = content.content_ordinal
            FROM order_operations.incorporation_contents AS content
            WHERE work.incorporation_id = content.incorporation_id
              AND work.product_id = content.product_id;
            """);

        migrationBuilder.Sql(
            """
            WITH ranked AS (
                SELECT idempotency_key,
                       product_id,
                       (ROW_NUMBER() OVER (
                           PARTITION BY idempotency_key
                           ORDER BY product_id))::integer AS line_ordinal
                FROM order_operations.first_confirmation_command_contents
            )
            UPDATE order_operations.first_confirmation_command_contents AS content
            SET line_ordinal = ranked.line_ordinal
            FROM ranked
            WHERE content.idempotency_key = ranked.idempotency_key
              AND content.product_id = ranked.product_id;
            """);

        migrationBuilder.Sql(
            """
            WITH ranked AS (
                SELECT idempotency_key,
                       product_id,
                       (ROW_NUMBER() OVER (
                           PARTITION BY idempotency_key
                           ORDER BY product_id))::integer AS line_ordinal
                FROM order_operations.subsequent_confirmation_command_contents
            )
            UPDATE order_operations.subsequent_confirmation_command_contents AS content
            SET line_ordinal = ranked.line_ordinal
            FROM ranked
            WHERE content.idempotency_key = ranked.idempotency_key
              AND content.product_id = ranked.product_id;
            """);

        migrationBuilder.AlterColumn<int>(
            name: "content_ordinal",
            schema: "order_operations",
            table: "incorporation_contents",
            type: "integer",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "content_ordinal",
            schema: "order_operations",
            table: "preparation_work",
            type: "integer",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "line_ordinal",
            schema: "order_operations",
            table: "first_confirmation_command_contents",
            type: "integer",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "line_ordinal",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents",
            type: "integer",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.DropForeignKey(
            name: "FK_order_operations_preparation_work_content",
            schema: "order_operations",
            table: "preparation_work");

        migrationBuilder.DropIndex(
            name: "UX_order_operations_preparation_work_content",
            schema: "order_operations",
            table: "preparation_work");

        migrationBuilder.DropPrimaryKey(
            name: "PK_order_operations_incorporation_contents",
            schema: "order_operations",
            table: "incorporation_contents");

        migrationBuilder.AddPrimaryKey(
            name: "PK_order_operations_incorporation_contents",
            schema: "order_operations",
            table: "incorporation_contents",
            columns: new[] { "incorporation_id", "content_ordinal" });

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_incorporation_contents_product",
            schema: "order_operations",
            table: "incorporation_contents",
            columns: new[] { "incorporation_id", "product_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_preparation_work_content",
            schema: "order_operations",
            table: "preparation_work",
            columns: new[] { "incorporation_id", "content_ordinal" },
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_order_operations_preparation_work_content",
            schema: "order_operations",
            table: "preparation_work",
            columns: new[] { "incorporation_id", "content_ordinal" },
            principalSchema: "order_operations",
            principalTable: "incorporation_contents",
            principalColumns: new[] { "incorporation_id", "content_ordinal" },
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.DropColumn(
            name: "product_id",
            schema: "order_operations",
            table: "preparation_work");

        migrationBuilder.DropPrimaryKey(
            name: "PK_order_operations_first_confirmation_command_contents",
            schema: "order_operations",
            table: "first_confirmation_command_contents");

        migrationBuilder.AddPrimaryKey(
            name: "PK_order_operations_first_confirmation_command_contents",
            schema: "order_operations",
            table: "first_confirmation_command_contents",
            columns: new[] { "idempotency_key", "line_ordinal" });

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_first_command_contents_product",
            schema: "order_operations",
            table: "first_confirmation_command_contents",
            columns: new[] { "idempotency_key", "product_id" },
            unique: true);

        migrationBuilder.DropPrimaryKey(
            name: "PK_order_operations_subsequent_confirmation_command_contents",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents");

        migrationBuilder.AddPrimaryKey(
            name: "PK_order_operations_subsequent_confirmation_command_contents",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents",
            columns: new[] { "idempotency_key", "line_ordinal" });

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_subsequent_command_contents_product",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents",
            columns: new[] { "idempotency_key", "product_id" },
            unique: true);

        migrationBuilder.AddCheckConstraint(
            name: "CK_order_operations_incorporation_contents_ordinal_positive",
            schema: "order_operations",
            table: "incorporation_contents",
            sql: "content_ordinal > 0");

        migrationBuilder.AddCheckConstraint(
            name: "CK_order_operations_first_command_contents_ordinal_positive",
            schema: "order_operations",
            table: "first_confirmation_command_contents",
            sql: "line_ordinal > 0");

        migrationBuilder.AddCheckConstraint(
            name: "CK_order_operations_subseq_command_contents_ordinal_positive",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents",
            sql: "line_ordinal > 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "product_id",
            schema: "order_operations",
            table: "preparation_work",
            type: "uuid",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE order_operations.preparation_work AS work
            SET product_id = content.product_id
            FROM order_operations.incorporation_contents AS content
            WHERE work.incorporation_id = content.incorporation_id
              AND work.content_ordinal = content.content_ordinal;
            """);

        migrationBuilder.AlterColumn<Guid>(
            name: "product_id",
            schema: "order_operations",
            table: "preparation_work",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.DropForeignKey(
            name: "FK_order_operations_preparation_work_content",
            schema: "order_operations",
            table: "preparation_work");

        migrationBuilder.DropIndex(
            name: "UX_order_operations_preparation_work_content",
            schema: "order_operations",
            table: "preparation_work");

        migrationBuilder.DropPrimaryKey(
            name: "PK_order_operations_incorporation_contents",
            schema: "order_operations",
            table: "incorporation_contents");

        migrationBuilder.DropIndex(
            name: "UX_order_operations_incorporation_contents_product",
            schema: "order_operations",
            table: "incorporation_contents");

        migrationBuilder.AddPrimaryKey(
            name: "PK_order_operations_incorporation_contents",
            schema: "order_operations",
            table: "incorporation_contents",
            columns: new[] { "incorporation_id", "product_id" });

        migrationBuilder.CreateIndex(
            name: "UX_order_operations_preparation_work_content",
            schema: "order_operations",
            table: "preparation_work",
            columns: new[] { "incorporation_id", "product_id" },
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_order_operations_preparation_work_content",
            schema: "order_operations",
            table: "preparation_work",
            columns: new[] { "incorporation_id", "product_id" },
            principalSchema: "order_operations",
            principalTable: "incorporation_contents",
            principalColumns: new[] { "incorporation_id", "product_id" },
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.DropCheckConstraint(
            name: "CK_order_operations_incorporation_contents_ordinal_positive",
            schema: "order_operations",
            table: "incorporation_contents");

        migrationBuilder.DropColumn(
            name: "content_ordinal",
            schema: "order_operations",
            table: "preparation_work");

        migrationBuilder.DropColumn(
            name: "content_ordinal",
            schema: "order_operations",
            table: "incorporation_contents");

        migrationBuilder.DropPrimaryKey(
            name: "PK_order_operations_first_confirmation_command_contents",
            schema: "order_operations",
            table: "first_confirmation_command_contents");

        migrationBuilder.DropIndex(
            name: "UX_order_operations_first_command_contents_product",
            schema: "order_operations",
            table: "first_confirmation_command_contents");

        migrationBuilder.AddPrimaryKey(
            name: "PK_order_operations_first_confirmation_command_contents",
            schema: "order_operations",
            table: "first_confirmation_command_contents",
            columns: new[] { "idempotency_key", "product_id" });

        migrationBuilder.DropCheckConstraint(
            name: "CK_order_operations_first_command_contents_ordinal_positive",
            schema: "order_operations",
            table: "first_confirmation_command_contents");

        migrationBuilder.DropColumn(
            name: "line_ordinal",
            schema: "order_operations",
            table: "first_confirmation_command_contents");

        migrationBuilder.DropPrimaryKey(
            name: "PK_order_operations_subsequent_confirmation_command_contents",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents");

        migrationBuilder.DropIndex(
            name: "UX_order_operations_subsequent_command_contents_product",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents");

        migrationBuilder.AddPrimaryKey(
            name: "PK_order_operations_subsequent_confirmation_command_contents",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents",
            columns: new[] { "idempotency_key", "product_id" });

        migrationBuilder.DropCheckConstraint(
            name: "CK_order_operations_subseq_command_contents_ordinal_positive",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents");

        migrationBuilder.DropColumn(
            name: "line_ordinal",
            schema: "order_operations",
            table: "subsequent_confirmation_command_contents");
    }
}
