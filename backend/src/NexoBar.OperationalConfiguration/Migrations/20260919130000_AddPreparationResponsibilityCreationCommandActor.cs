using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OperationalConfiguration.Migrations;

[DbContext(typeof(OperationalConfigurationDbContext))]
[Migration("20260919130000_AddPreparationResponsibilityCreationCommandActor")]
public partial class AddPreparationResponsibilityCreationCommandActor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "actor_identity_id",
            schema: "operational_configuration",
            table: "preparation_responsibility_creation_commands",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "command_kind",
            schema: "operational_configuration",
            table: "preparation_responsibility_creation_commands",
            type: "text",
            nullable: false,
            defaultValue: "Create");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "actor_identity_id",
            schema: "operational_configuration",
            table: "preparation_responsibility_creation_commands");

        migrationBuilder.DropColumn(
            name: "command_kind",
            schema: "operational_configuration",
            table: "preparation_responsibility_creation_commands");
    }
}
