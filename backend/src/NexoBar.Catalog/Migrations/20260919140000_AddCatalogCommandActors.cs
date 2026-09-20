using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.Catalog.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20260919140000_AddCatalogCommandActors")]
public partial class AddCatalogCommandActors : Migration
{
    private static readonly string[] CommandTables =
    [
        "product_creation_commands",
        "product_price_change_commands",
        "product_preparation_configuration_change_commands"
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in CommandTables)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "actor_identity_id",
                schema: "catalog",
                table: table,
                type: "uuid",
                nullable: true);
        }

        migrationBuilder.AddColumn<string>(
            name: "command_kind", schema: "catalog", table: CommandTables[0],
            type: "text", nullable: false, defaultValue: "CreateProduct");
        migrationBuilder.AddColumn<string>(
            name: "command_kind", schema: "catalog", table: CommandTables[1],
            type: "text", nullable: false, defaultValue: "ChangeProductPrice");
        migrationBuilder.AddColumn<string>(
            name: "command_kind", schema: "catalog", table: CommandTables[2],
            type: "text", nullable: false,
            defaultValue: "ChangeProductPreparationConfiguration");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in CommandTables)
        {
            migrationBuilder.DropColumn(
                name: "actor_identity_id", schema: "catalog", table: table);
            migrationBuilder.DropColumn(
                name: "command_kind", schema: "catalog", table: table);
        }
    }
}
