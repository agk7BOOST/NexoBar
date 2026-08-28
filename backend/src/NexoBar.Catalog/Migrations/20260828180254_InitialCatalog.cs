using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "products",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operational_name = table.Column<string>(type: "text", nullable: false),
                    normalized_operational_name = table.Column<string>(type: "text", nullable: false, computedColumnSql: "lower(operational_name)", stored: true),
                    price = table.Column<decimal>(type: "numeric", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_available = table.Column<bool>(type: "boolean", nullable: false),
                    requires_preparation = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_products", x => x.id);
                    table.CheckConstraint("CK_catalog_products_price_non_negative", "price >= 0");
                    table.CheckConstraint("CK_catalog_products_requires_preparation_i1", "requires_preparation = false");
                });

            migrationBuilder.CreateTable(
                name: "product_creation_commands",
                schema: "catalog",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_operational_name = table.Column<string>(type: "text", nullable: false),
                    intent_price = table.Column<decimal>(type: "numeric", nullable: false),
                    intent_requires_preparation = table.Column<bool>(type: "boolean", nullable: false),
                    result_product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_is_active = table.Column<bool>(type: "boolean", nullable: false),
                    result_is_available = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_product_creation_commands", x => x.idempotency_key);
                    table.CheckConstraint("CK_catalog_product_creation_commands_i1_result", "intent_requires_preparation = false AND result_is_active = true AND result_is_available = true");
                    table.CheckConstraint("CK_catalog_product_creation_commands_price_non_negative", "intent_price >= 0");
                    table.ForeignKey(
                        name: "FK_catalog_product_creation_commands_products",
                        column: x => x.result_product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_catalog_product_creation_commands_result_product_id",
                schema: "catalog",
                table: "product_creation_commands",
                column: "result_product_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_catalog_products_active_normalized_operational_name",
                schema: "catalog",
                table: "products",
                column: "normalized_operational_name",
                unique: true,
                filter: "is_active");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_creation_commands",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "products",
                schema: "catalog");
        }
    }
}
