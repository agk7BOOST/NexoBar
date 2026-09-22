using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogGroupsAndProductLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE catalog.groups (id uuid NOT NULL CONSTRAINT "PK_catalog_groups" PRIMARY KEY, operational_name text NOT NULL, normalized_operational_name text GENERATED ALWAYS AS (lower(operational_name)) STORED NOT NULL, CONSTRAINT "CK_catalog_groups_operational_name_not_blank" CHECK (length(btrim(operational_name)) > 0));
                CREATE UNIQUE INDEX "UX_catalog_groups_normalized_operational_name" ON catalog.groups (normalized_operational_name);
                ALTER TABLE catalog.products ADD COLUMN group_id uuid NULL;
                ALTER TABLE catalog.products ADD CONSTRAINT "FK_catalog_products_groups" FOREIGN KEY (group_id) REFERENCES catalog.groups (id) ON DELETE RESTRICT;
                CREATE INDEX "IX_catalog_products_group_id" ON catalog.products (group_id);
                CREATE TABLE catalog.group_creation_commands (idempotency_key uuid NOT NULL CONSTRAINT "PK_catalog_group_creation_commands" PRIMARY KEY, actor_identity_id uuid NULL, command_kind text NOT NULL, intent_operational_name text NOT NULL, result_group_id uuid NOT NULL UNIQUE, CONSTRAINT "CK_catalog_group_creation_commands_intent_name_not_blank" CHECK (length(btrim(intent_operational_name)) > 0), CONSTRAINT "FK_catalog_group_creation_commands_groups" FOREIGN KEY (result_group_id) REFERENCES catalog.groups (id) ON DELETE RESTRICT);
                CREATE TABLE catalog.product_group_change_commands (idempotency_key uuid NOT NULL CONSTRAINT "PK_catalog_product_group_change_commands" PRIMARY KEY, actor_identity_id uuid NULL, command_kind text NOT NULL, product_id uuid NOT NULL, intent_expected_group_id uuid NULL, intent_new_group_id uuid NULL, result_group_id uuid NULL, CONSTRAINT "CK_catalog_product_group_change_commands_result_matches_intent" CHECK (result_group_id IS NOT DISTINCT FROM intent_new_group_id), CONSTRAINT "FK_catalog_product_group_change_commands_products" FOREIGN KEY (product_id) REFERENCES catalog.products (id) ON DELETE RESTRICT);
                CREATE TABLE catalog.product_operational_name_change_commands (idempotency_key uuid NOT NULL CONSTRAINT "PK_catalog_product_operational_name_change_commands" PRIMARY KEY, actor_identity_id uuid NULL, command_kind text NOT NULL, product_id uuid NOT NULL, intent_expected_operational_name text NOT NULL, intent_new_operational_name text NOT NULL, result_operational_name text NOT NULL, CONSTRAINT "FK_catalog_product_operational_name_change_commands_products" FOREIGN KEY (product_id) REFERENCES catalog.products (id) ON DELETE RESTRICT);
                CREATE TABLE catalog.product_retire_commands (idempotency_key uuid NOT NULL CONSTRAINT "PK_catalog_product_retire_commands" PRIMARY KEY, actor_identity_id uuid NULL, command_kind text NOT NULL, product_id uuid NOT NULL, result_is_active boolean NOT NULL, result_is_available boolean NOT NULL, CONSTRAINT "CK_catalog_product_retire_commands_result" CHECK (result_is_active = false), CONSTRAINT "FK_catalog_product_retire_commands_products" FOREIGN KEY (product_id) REFERENCES catalog.products (id) ON DELETE RESTRICT);
                CREATE TABLE catalog.product_reactivate_commands (idempotency_key uuid NOT NULL CONSTRAINT "PK_catalog_product_reactivate_commands" PRIMARY KEY, actor_identity_id uuid NULL, command_kind text NOT NULL, product_id uuid NOT NULL, result_is_active boolean NOT NULL, result_is_available boolean NOT NULL, CONSTRAINT "CK_catalog_product_reactivate_commands_result" CHECK (result_is_active = true AND result_is_available = true), CONSTRAINT "FK_catalog_product_reactivate_commands_products" FOREIGN KEY (product_id) REFERENCES catalog.products (id) ON DELETE RESTRICT);
                """);
            migrationBuilder.CreateIndex(
                name: "IX_product_retire_commands_product_id",
                schema: "catalog",
                table: "product_retire_commands",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_reactivate_commands_product_id",
                schema: "catalog",
                table: "product_reactivate_commands",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_operational_name_change_commands_product_id",
                schema: "catalog",
                table: "product_operational_name_change_commands",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_group_change_commands_product_id",
                schema: "catalog",
                table: "product_group_change_commands",
                column: "product_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_product_retire_commands_product_id",
                schema: "catalog",
                table: "product_retire_commands");

            migrationBuilder.DropIndex(
                name: "IX_product_reactivate_commands_product_id",
                schema: "catalog",
                table: "product_reactivate_commands");

            migrationBuilder.DropIndex(
                name: "IX_product_operational_name_change_commands_product_id",
                schema: "catalog",
                table: "product_operational_name_change_commands");

            migrationBuilder.DropIndex(
                name: "IX_product_group_change_commands_product_id",
                schema: "catalog",
                table: "product_group_change_commands");

            migrationBuilder.Sql("""
                DROP TABLE catalog.product_reactivate_commands;
                DROP TABLE catalog.product_retire_commands;
                DROP TABLE catalog.product_operational_name_change_commands;
                DROP TABLE catalog.product_group_change_commands;
                DROP TABLE catalog.group_creation_commands;
                ALTER TABLE catalog.products DROP CONSTRAINT "FK_catalog_products_groups";
                ALTER TABLE catalog.products DROP COLUMN group_id;
                DROP TABLE catalog.groups;
                """);
        }
    }
}
