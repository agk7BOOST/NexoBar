using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.Catalog.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20260923120000_AddEligibleProductDelete")]
public partial class AddEligibleProductDelete : Migration
{
    private static readonly (string Table, string Constraint, string Column)[] ProductReferences =
    [
        ("product_creation_commands", "FK_catalog_product_creation_commands_products", "result_product_id"),
        ("product_price_change_commands", "FK_catalog_product_price_change_commands_products", "product_id"),
        ("product_preparation_configuration_change_commands", "FK_catalog_product_prep_config_cmd_products", "product_id"),
        ("product_group_change_commands", "FK_catalog_product_group_change_commands_products", "product_id"),
        ("product_operational_name_change_commands", "FK_catalog_product_operational_name_change_commands_products", "product_id"),
        ("product_retire_commands", "FK_catalog_product_retire_commands_products", "product_id"),
        ("product_reactivate_commands", "FK_catalog_product_reactivate_commands_products", "product_id"),
        ("product_availability_change_commands", "FK_catalog_product_availability_change_commands_products", "product_id")
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var (table, constraint, _) in ProductReferences)
            migrationBuilder.DropForeignKey(constraint, table, "catalog");

        migrationBuilder.CreateTable(
            name: "product_delete_commands",
            schema: "catalog",
            columns: table => new
            {
                idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                actor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                command_kind = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
                table.PrimaryKey("PK_catalog_product_delete_commands", x => x.idempotency_key));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("product_delete_commands", "catalog");
        foreach (var (table, constraint, column) in ProductReferences)
            migrationBuilder.AddForeignKey(
                name: constraint,
                schema: "catalog",
                table: table,
                column: column,
                principalSchema: "catalog",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
    }
}
