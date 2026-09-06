using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations;

public partial class AddContentQuantityState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "content_quantity_states",
            schema: "order_operations",
            columns: table => new
            {
                incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                content_ordinal = table.Column<int>(type: "integer", nullable: false),
                removed_by_correction_quantity = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_content_quantity_states",
                    x => new { x.incorporation_id, x.content_ordinal });
                table.CheckConstraint(
                    "CK_content_quantity_states_removed_non_negative",
                    "removed_by_correction_quantity >= 0");
                table.ForeignKey(
                    name: "FK_content_quantity_states_content",
                    columns: x => new { x.incorporation_id, x.content_ordinal },
                    principalSchema: "order_operations",
                    principalTable: "incorporation_contents",
                    principalColumns: new[] { "incorporation_id", "content_ordinal" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.Sql(
            """
            INSERT INTO order_operations.content_quantity_states
                (incorporation_id, content_ordinal, removed_by_correction_quantity)
            SELECT incorporation_id, content_ordinal, 0
            FROM order_operations.incorporation_contents;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $migration$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM order_operations.content_quantity_states
                    WHERE removed_by_correction_quantity <> 0)
                THEN
                    RAISE EXCEPTION 'Cannot remove ContentQuantityState while non-zero correction state exists.';
                END IF;
            END
            $migration$;
            """);

        migrationBuilder.DropTable(
            name: "content_quantity_states",
            schema: "order_operations");
    }
}
