using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoBar.OrderOperations.Migrations;

[DbContext(typeof(OrderOperationsDbContext))]
[Migration("20260922140000_AddConfiguredOrderContexts")]
public partial class AddConfiguredOrderContexts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "current_context_id", schema: "order_operations", table: "orders", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "confirmed_context_id", schema: "order_operations", table: "confirmation_history", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "intent_context_id", schema: "order_operations", table: "first_confirmation_commands", type: "uuid", nullable: true);
        migrationBuilder.Sql(@"
UPDATE order_operations.orders o SET current_context_id = c.id
FROM operational_configuration.contexts c
WHERE lower(btrim(o.context)) = c.normalized_operational_name;
UPDATE order_operations.confirmation_history h SET confirmed_context_id = c.id
FROM operational_configuration.contexts c
WHERE lower(btrim(h.confirmed_context)) = c.normalized_operational_name;
UPDATE order_operations.first_confirmation_commands f SET intent_context_id = c.id
FROM operational_configuration.contexts c
WHERE lower(btrim(f.intent_context)) = c.normalized_operational_name;
DO $migration$
BEGIN
  IF EXISTS (SELECT 1 FROM order_operations.orders WHERE current_context_id IS NULL)
     OR EXISTS (SELECT 1 FROM order_operations.confirmation_history WHERE confirmed_context_id IS NULL)
     OR EXISTS (SELECT 1 FROM order_operations.first_confirmation_commands WHERE intent_context_id IS NULL) THEN
    RAISE EXCEPTION 'Configured Context backfill left persisted OrderOperations Context meaning unmapped';
  END IF;
END
$migration$;");
        migrationBuilder.AlterColumn<Guid>(name: "current_context_id", schema: "order_operations", table: "orders", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
        migrationBuilder.AlterColumn<Guid>(name: "confirmed_context_id", schema: "order_operations", table: "confirmation_history", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
        migrationBuilder.AlterColumn<Guid>(name: "intent_context_id", schema: "order_operations", table: "first_confirmation_commands", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "current_context_id", schema: "order_operations", table: "orders");
        migrationBuilder.DropColumn(name: "confirmed_context_id", schema: "order_operations", table: "confirmation_history");
        migrationBuilder.DropColumn(name: "intent_context_id", schema: "order_operations", table: "first_confirmation_commands");
    }
}
