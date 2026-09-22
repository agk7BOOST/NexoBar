using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace NexoBar.Catalog.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20260922050000_AddProductAvailabilityChangeCommands")]
public partial class AddProductAvailabilityChangeCommands : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "product_availability_change_commands",
            schema: "catalog",
            columns: table => new
            {
                idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                actor_identity_id = table.Column<Guid>(type: "uuid", nullable: true),
                command_kind = table.Column<string>(type: "text", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                intent_expected_current_availability = table.Column<bool>(type: "boolean", nullable: false),
                intent_new_availability = table.Column<bool>(type: "boolean", nullable: false),
                result_availability = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_catalog_product_availability_change_commands", x => x.idempotency_key);
                table.CheckConstraint(
                    "CK_catalog_product_availability_change_commands_result_matches_intent",
                    "result_availability = intent_new_availability");
                table.ForeignKey(
                    "FK_catalog_product_availability_change_commands_products",
                    x => x.product_id,
                    "products",
                    "id",
                    principalSchema: "catalog",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_product_availability_change_commands_product_id",
            schema: "catalog",
            table: "product_availability_change_commands",
            column: "product_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "product_availability_change_commands",
            schema: "catalog");
    }
}
