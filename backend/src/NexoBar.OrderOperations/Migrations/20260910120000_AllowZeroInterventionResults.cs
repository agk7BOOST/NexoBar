using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoBar.OrderOperations.Migrations;

public partial class AllowZeroInterventionResults : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("CK_preparation_history_result_total_positive", "preparation_history", "order_operations");
        migrationBuilder.DropCheckConstraint("CK_preparation_commands_result_total_positive", "preparation_commands", "order_operations");
        migrationBuilder.AddCheckConstraint("CK_preparation_history_result_total_positive", "preparation_history", "resulting_total_quantity >= 0", "order_operations");
        migrationBuilder.AddCheckConstraint("CK_preparation_commands_result_total_positive", "preparation_commands", "result_total_quantity >= 0", "order_operations");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM order_operations.preparation_history WHERE resulting_total_quantity = 0)
                   OR EXISTS (SELECT 1 FROM order_operations.preparation_commands WHERE result_total_quantity = 0) THEN
                    RAISE EXCEPTION 'Cannot restore positive Preparation results while zero intervention results exist';
                END IF;
            END $$;
            """);
        migrationBuilder.DropCheckConstraint("CK_preparation_history_result_total_positive", "preparation_history", "order_operations");
        migrationBuilder.DropCheckConstraint("CK_preparation_commands_result_total_positive", "preparation_commands", "order_operations");
        migrationBuilder.AddCheckConstraint("CK_preparation_history_result_total_positive", "preparation_history", "resulting_total_quantity > 0", "order_operations");
        migrationBuilder.AddCheckConstraint("CK_preparation_commands_result_total_positive", "preparation_commands", "result_total_quantity > 0", "order_operations");
    }
}
