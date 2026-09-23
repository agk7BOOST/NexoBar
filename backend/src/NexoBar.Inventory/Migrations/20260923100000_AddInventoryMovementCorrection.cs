using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoBar.Inventory.Migrations;

[DbContext(typeof(InventoryDbContext))]
[Migration("20260923100000_AddInventoryMovementCorrection")]
public sealed class AddInventoryMovementCorrection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "movement_corrections", schema: "inventory",
        columns: table => new
        {
            idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
            actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
            root_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
            inventory_item_id = table.Column<Guid>(type: "uuid", nullable: false),
            sequence = table.Column<long>(type: "bigint", nullable: false),
            movement_revision = table.Column<long>(type: "bigint", nullable: false),
            previous_nature = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
            previous_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
            corrected_nature = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
            corrected_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: false),
            delta_applied = table.Column<decimal>(type: "numeric(29,12)", nullable: false),
            resulting_registered_quantity = table.Column<decimal>(type: "numeric(28,12)", nullable: true),
            occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_inventory_movement_corrections", x => x.idempotency_key);
            table.CheckConstraint("CK_inventory_movement_corrections_sequence", "sequence > 0 AND movement_revision > 0");
            table.CheckConstraint("CK_inventory_movement_corrections_nature", "previous_nature IN ('Entry','ManualExit','Waste') AND corrected_nature IN ('Entry','ManualExit','Waste')");
            table.CheckConstraint("CK_inventory_movement_corrections_quantity", "previous_quantity >= 0 AND corrected_quantity >= 0");
            table.ForeignKey(name: "FK_inventory_movement_corrections_root", column: x => x.root_movement_id, principalSchema: "inventory", principalTable: "inventory_movements", principalColumn: "id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey(name: "FK_inventory_movement_corrections_item", column: x => x.inventory_item_id, principalSchema: "inventory", principalTable: "inventory_items", principalColumn: "id", onDelete: ReferentialAction.Restrict);
        });

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("movement_corrections", "inventory");
}
