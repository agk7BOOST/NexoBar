using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoBar.OrderOperations.Migrations;

[DbContext(typeof(OrderOperationsDbContext))]
[Migration("20260922150000_AddOrderContextChanges")]
public partial class AddOrderContextChanges : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "order_context_change_history",
            schema: "order_operations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                sequence = table.Column<int>(type: "integer", nullable: false),
                previous_context_id = table.Column<Guid>(type: "uuid", nullable: false),
                previous_context_operational_name = table.Column<string>(type: "text", nullable: false),
                new_context_id = table.Column<Guid>(type: "uuid", nullable: false),
                new_context_operational_name = table.Column<string>(type: "text", nullable: false),
                actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_order_context_change_history", x => x.id);
                table.CheckConstraint("CK_order_context_change_history_sequence_positive", "sequence > 0");
                table.CheckConstraint("CK_order_context_change_history_names_not_empty", "length(btrim(previous_context_operational_name)) > 0 AND length(btrim(new_context_operational_name)) > 0");
                table.ForeignKey("FK_order_context_change_history_order", x => x.order_id, "orders", "id", "order_operations", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex("UX_order_context_change_history_order_sequence", "order_context_change_history", new[] { "order_id", "sequence" }, "order_operations", unique: true);

        migrationBuilder.CreateTable(
            name: "order_context_change_commands",
            schema: "order_operations",
            columns: table => new
            {
                idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                expected_current_context_id = table.Column<Guid>(type: "uuid", nullable: false),
                new_context_id = table.Column<Guid>(type: "uuid", nullable: false),
                result_previous_context_id = table.Column<Guid>(type: "uuid", nullable: false),
                result_previous_context_operational_name = table.Column<string>(type: "text", nullable: false),
                result_current_context_id = table.Column<Guid>(type: "uuid", nullable: false),
                result_current_context_operational_name = table.Column<string>(type: "text", nullable: false),
                result_occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_order_context_change_commands", x => x.idempotency_key);
                table.CheckConstraint("CK_order_context_change_commands_result_names_not_empty", "length(btrim(result_previous_context_operational_name)) > 0 AND length(btrim(result_current_context_operational_name)) > 0");
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "order_context_change_commands", schema: "order_operations");
        migrationBuilder.DropTable(name: "order_context_change_history", schema: "order_operations");
    }
}
