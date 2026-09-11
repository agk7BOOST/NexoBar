using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations
{
    /// <inheritdoc />
    public partial class AddAppliedPriceCorrection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "applied_price_correction_history",
                schema: "order_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_ordinal = table.Column<int>(type: "integer", nullable: false),
                    previous_effective_applied_price = table.Column<decimal>(type: "numeric", nullable: false),
                    resulting_effective_applied_price = table.Column<decimal>(type: "numeric", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applied_price_correction_history", x => x.id);
                    table.CheckConstraint("CK_applied_price_correction_history_kind", "event_kind = 'AppliedPriceCorrected'");
                    table.CheckConstraint("CK_applied_price_correction_history_prices", "previous_effective_applied_price >= 0 AND resulting_effective_applied_price >= 0 AND previous_effective_applied_price <> resulting_effective_applied_price");
                    table.ForeignKey(
                        name: "FK_applied_price_correction_history_content",
                        columns: x => new { x.incorporation_id, x.content_ordinal },
                        principalSchema: "order_operations",
                        principalTable: "incorporation_contents",
                        principalColumns: new[] { "incorporation_id", "content_ordinal" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_applied_price_correction_history_order",
                        column: x => x.order_id,
                        principalSchema: "order_operations",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "content_applied_price_states",
                schema: "order_operations",
                columns: table => new
                {
                    incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_ordinal = table.Column<int>(type: "integer", nullable: false),
                    effective_applied_price = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_applied_price_states", x => new { x.incorporation_id, x.content_ordinal });
                    table.CheckConstraint("CK_content_applied_price_states_effective_non_negative", "effective_applied_price >= 0");
                    table.ForeignKey(
                        name: "FK_content_applied_price_states_content",
                        columns: x => new { x.incorporation_id, x.content_ordinal },
                        principalSchema: "order_operations",
                        principalTable: "incorporation_contents",
                        principalColumns: new[] { "incorporation_id", "content_ordinal" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "applied_price_correction_commands",
                schema: "order_operations",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incorporation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_ordinal = table.Column<int>(type: "integer", nullable: false),
                    result_history_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_previous_effective_applied_price = table.Column<decimal>(type: "numeric", nullable: false),
                    result_effective_applied_price = table.Column<decimal>(type: "numeric", nullable: false),
                    result_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applied_price_correction_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_applied_price_correction_commands_kind", "command_kind = 'ApplyCurrentCatalogPrice'");
                    table.ForeignKey(
                        name: "FK_applied_price_correction_commands_history",
                        column: x => x.result_history_id,
                        principalSchema: "order_operations",
                        principalTable: "applied_price_correction_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                INSERT INTO order_operations.content_applied_price_states
                    (incorporation_id, content_ordinal, effective_applied_price)
                SELECT incorporation_id, content_ordinal, applied_price
                FROM order_operations.incorporation_contents;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_applied_price_correction_commands_history",
                schema: "order_operations",
                table: "applied_price_correction_commands",
                column: "result_history_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_applied_price_correction_history_content_time",
                schema: "order_operations",
                table: "applied_price_correction_history",
                columns: new[] { "incorporation_id", "content_ordinal", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_applied_price_correction_history_order_id",
                schema: "order_operations",
                table: "applied_price_correction_history",
                column: "order_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM order_operations.applied_price_correction_history)
                       OR EXISTS (SELECT 1 FROM order_operations.applied_price_correction_commands)
                       OR EXISTS (SELECT 1 FROM order_operations.content_applied_price_states s JOIN order_operations.incorporation_contents c ON c.incorporation_id = s.incorporation_id AND c.content_ordinal = s.content_ordinal WHERE s.effective_applied_price <> c.applied_price) THEN
                        RAISE EXCEPTION 'Cannot remove meaningful Applied Price Correction State or History';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "applied_price_correction_commands",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "content_applied_price_states",
                schema: "order_operations");

            migrationBuilder.DropTable(
                name: "applied_price_correction_history",
                schema: "order_operations");
        }
    }
}
