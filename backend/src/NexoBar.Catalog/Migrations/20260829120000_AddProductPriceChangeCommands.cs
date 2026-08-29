using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.Catalog.Migrations
{
    public partial class AddProductPriceChangeCommands : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_price_change_commands",
                schema: "catalog",
                columns: table => new
                {
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_expected_current_price = table.Column<decimal>(type: "numeric", nullable: false),
                    intent_new_price = table.Column<decimal>(type: "numeric", nullable: false),
                    result_price = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_catalog_product_price_change_commands",
                        x => x.idempotency_key);
                    table.CheckConstraint(
                        "CK_catalog_product_price_change_commands_intent_new_price_non_negative",
                        "intent_new_price >= 0");
                    table.CheckConstraint(
                        "CK_catalog_product_price_change_commands_result_matches_intent",
                        "result_price = intent_new_price");
                    table.CheckConstraint(
                        "CK_catalog_product_price_change_commands_result_price_non_negative",
                        "result_price >= 0");
                    table.ForeignKey(
                        name: "FK_catalog_product_price_change_commands_products",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_price_change_commands_product_id",
                schema: "catalog",
                table: "product_price_change_commands",
                column: "product_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_price_change_commands",
                schema: "catalog");
        }
    }
}
