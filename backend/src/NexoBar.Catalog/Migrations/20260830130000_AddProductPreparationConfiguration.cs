using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.Catalog.Migrations;

public partial class AddProductPreparationConfiguration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_catalog_products_requires_preparation_i1",
            schema: "catalog",
            table: "products");

        migrationBuilder.AddColumn<Guid>(
            name: "preparation_responsibility_id",
            schema: "catalog",
            table: "products",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "CK_catalog_products_preparation_configuration_coherent",
            schema: "catalog",
            table: "products",
            sql: "(requires_preparation = false AND " +
                "preparation_responsibility_id IS NULL) OR " +
                "(requires_preparation = true AND " +
                "preparation_responsibility_id IS NOT NULL)");

        migrationBuilder.CreateTable(
            name: "product_preparation_configuration_change_commands",
            schema: "catalog",
            columns: table => new
            {
                idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                intent_expected_responsibility_id = table.Column<Guid>(
                    type: "uuid", nullable: true),
                intent_new_responsibility_id = table.Column<Guid>(
                    type: "uuid", nullable: true),
                result_responsibility_id = table.Column<Guid>(
                    type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_catalog_product_preparation_configuration_change_commands",
                    x => x.idempotency_key);
                table.CheckConstraint(
                    "CK_catalog_product_prep_config_cmd_result_matches_intent",
                    "result_responsibility_id IS NOT DISTINCT FROM " +
                    "intent_new_responsibility_id");
                table.ForeignKey(
                    name: "FK_catalog_product_prep_config_cmd_products",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_catalog_product_prep_config_cmd_product",
            schema: "catalog",
            table: "product_preparation_configuration_change_commands",
            column: "product_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "product_preparation_configuration_change_commands",
            schema: "catalog");
        migrationBuilder.DropCheckConstraint(
            name: "CK_catalog_products_preparation_configuration_coherent",
            schema: "catalog",
            table: "products");
        migrationBuilder.DropColumn(
            name: "preparation_responsibility_id",
            schema: "catalog",
            table: "products");
        migrationBuilder.AddCheckConstraint(
            name: "CK_catalog_products_requires_preparation_i1",
            schema: "catalog",
            table: "products",
            sql: "requires_preparation = false");
    }
}
