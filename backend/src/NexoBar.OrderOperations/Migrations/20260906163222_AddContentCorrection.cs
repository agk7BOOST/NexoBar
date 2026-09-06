using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations
{
    /// <inheritdoc />
    public partial class AddContentCorrection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_order_operations_preparation_work_total_positive",
                schema: "order_operations",
                table: "preparation_work");

            migrationBuilder.CreateTable(
                name: "content_correction_history",
                schema: "order_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_ordinal = table.Column<int>(type: "integer", nullable: false),
                    corrected_quantity = table.Column<int>(type: "integer", nullable: false),
                    confirmed_quantity = table.Column<int>(type: "integer", nullable: false),
                    previous_removed_by_correction_quantity = table.Column<int>(type: "integer", nullable: false),
                    resulting_removed_by_correction_quantity = table.Column<int>(type: "integer", nullable: false),
                    previous_fulfillment_quantity = table.Column<int>(type: "integer", nullable: false),
                    resulting_fulfillment_quantity = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_correction_history", x => x.id);
                    table.CheckConstraint("CK_content_correction_history_kind", "event_kind = 'ContentQuantityCorrected'");
                    table.CheckConstraint("CK_content_correction_history_quantities", "corrected_quantity > 0 AND confirmed_quantity > 0 AND previous_removed_by_correction_quantity >= 0 AND resulting_fulfillment_quantity >= 0 AND previous_removed_by_correction_quantity::bigint + corrected_quantity::bigint = resulting_removed_by_correction_quantity::bigint AND confirmed_quantity::bigint - previous_removed_by_correction_quantity::bigint = previous_fulfillment_quantity::bigint AND confirmed_quantity::bigint - resulting_removed_by_correction_quantity::bigint = resulting_fulfillment_quantity::bigint");
                    table.ForeignKey(
                        name: "FK_content_correction_history_content",
                        columns: x => new { x.incorporation_id, x.content_ordinal },
                        principalSchema: "order_operations",
                        principalTable: "incorporation_contents",
                        principalColumns: new[] { "incorporation_id", "content_ordinal" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_content_correction_history_order",
                        column: x => x.order_id,
                        principalSchema: "order_operations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "content_correction_commands",
                schema: "order_operations",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_ordinal = table.Column<int>(type: "integer", nullable: false),
                    corrected_quantity = table.Column<int>(type: "integer", nullable: false),
                    result_history_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmed_quantity = table.Column<int>(type: "integer", nullable: false),
                    previous_removed_by_correction_quantity = table.Column<int>(type: "integer", nullable: false),
                    resulting_removed_by_correction_quantity = table.Column<int>(type: "integer", nullable: false),
                    previous_fulfillment_quantity = table.Column<int>(type: "integer", nullable: false),
                    resulting_fulfillment_quantity = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_correction_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_content_correction_commands_kind", "command_kind = 'CorrectContentQuantity'");
                    table.CheckConstraint("CK_content_correction_commands_quantities", "corrected_quantity > 0 AND confirmed_quantity > 0 AND previous_removed_by_correction_quantity >= 0 AND resulting_fulfillment_quantity >= 0 AND previous_removed_by_correction_quantity::bigint + corrected_quantity::bigint = resulting_removed_by_correction_quantity::bigint AND confirmed_quantity::bigint - previous_removed_by_correction_quantity::bigint = previous_fulfillment_quantity::bigint AND confirmed_quantity::bigint - resulting_removed_by_correction_quantity::bigint = resulting_fulfillment_quantity::bigint");
                    table.ForeignKey(
                        name: "FK_content_correction_commands_history",
                        column: x => x.result_history_id,
                        principalSchema: "order_operations",
                        principalTable: "content_correction_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_order_operations_preparation_work_total_positive",
                schema: "order_operations",
                table: "preparation_work",
                sql: "total_quantity >= 0");

            migrationBuilder.CreateIndex(
                name: "UX_content_correction_commands_history",
                schema: "order_operations",
                table: "content_correction_commands",
                column: "result_history_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_content_correction_history_content",
                schema: "order_operations",
                table: "content_correction_history",
                columns: new[] { "incorporation_id", "content_ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_content_correction_history_order",
                schema: "order_operations",
                table: "content_correction_history",
                columns: new[] { "order_id", "occurred_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM order_operations.content_correction_history)
                       OR EXISTS (SELECT 1 FROM order_operations.preparation_work WHERE total_quantity = 0) THEN
                        RAISE EXCEPTION 'Cannot remove Content Correction with retained correction History or zero-obligation Work';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "content_correction_commands",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "content_correction_history",
                schema: "order_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_order_operations_preparation_work_total_positive",
                schema: "order_operations",
                table: "preparation_work");

            migrationBuilder.AddCheckConstraint(
                name: "CK_order_operations_preparation_work_total_positive",
                schema: "order_operations",
                table: "preparation_work",
                sql: "total_quantity > 0");
        }
    }
}
