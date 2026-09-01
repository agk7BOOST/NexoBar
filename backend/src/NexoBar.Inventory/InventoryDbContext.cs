using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.Inventory;

internal sealed class InventoryDbContext(
    DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    internal DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();

    internal DbSet<InventoryItemCreationCommand> InventoryItemCreationCommands =>
        Set<InventoryItemCreationCommand>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("inventory");
        modelBuilder.ApplyConfiguration(new InventoryItemConfiguration());
        modelBuilder.ApplyConfiguration(new InventoryItemCreationCommandConfiguration());
    }

    private sealed class InventoryItemConfiguration :
        IEntityTypeConfiguration<InventoryItem>
    {
        public void Configure(EntityTypeBuilder<InventoryItem> builder)
        {
            builder.ToTable(
                "inventory_items",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_inventory_items_name_valid",
                        "length(operational_name) BETWEEN 1 AND 200 AND " +
                        "operational_name = btrim(operational_name) AND " +
                        "position(chr(10) in operational_name) = 0 AND " +
                        "position(chr(13) in operational_name) = 0");
                    table.HasCheckConstraint(
                        "CK_inventory_items_unit_valid",
                        "length(operational_unit) BETWEEN 1 AND 100 AND " +
                        "operational_unit = btrim(operational_unit) AND " +
                        "position(chr(10) in operational_unit) = 0 AND " +
                        "position(chr(13) in operational_unit) = 0");
                    table.HasCheckConstraint(
                        "CK_inventory_items_revision_non_negative",
                        "movement_revision >= 0");
                });
            builder.HasKey(item => item.Id)
                .HasName("PK_inventory_items");
            builder.Property(item => item.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            builder.Property(item => item.OperationalName)
                .HasColumnName("operational_name")
                .HasMaxLength(InventoryItem.OperationalNameMaximumLength)
                .IsRequired();
            builder.Property(item => item.NormalizedOperationalName)
                .HasColumnName("normalized_operational_name")
                .HasMaxLength(InventoryItem.OperationalNameMaximumLength)
                .IsRequired();
            builder.Property(item => item.OperationalUnit)
                .HasColumnName("operational_unit")
                .HasMaxLength(OperationalUnit.MaximumLength)
                .HasConversion(
                    unit => unit.Value,
                    value => OperationalUnit.FromPersisted(value))
                .IsRequired();
            builder.Property(item => item.CurrentRegisteredQuantity)
                .HasColumnName("current_registered_quantity")
                .HasColumnType("numeric(28,12)");
            builder.Property(item => item.MovementRevision)
                .HasColumnName("movement_revision")
                .HasColumnType("bigint")
                .IsRequired();
            builder.HasIndex(item => item.NormalizedOperationalName)
                .HasDatabaseName("UX_inventory_items_normalized_name")
                .IsUnique();
        }
    }

    private sealed class InventoryItemCreationCommandConfiguration :
        IEntityTypeConfiguration<InventoryItemCreationCommand>
    {
        public void Configure(EntityTypeBuilder<InventoryItemCreationCommand> builder)
        {
            builder.ToTable(
                "item_creation_commands",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_inventory_item_commands_kind",
                        "command_kind = 'CreateInventoryItem'");
                    table.HasCheckConstraint(
                        "CK_inventory_item_commands_revision",
                        "result_movement_revision >= 0");
                });
            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_inventory_item_creation_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key")
                .ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id")
                .ValueGeneratedNever();
            builder.Property(command => command.CommandKind)
                .HasColumnName("command_kind")
                .HasMaxLength(40)
                .IsRequired();
            builder.Property(command => command.IntentNormalizedOperationalName)
                .HasColumnName("intent_normalized_operational_name")
                .HasMaxLength(InventoryItem.OperationalNameMaximumLength)
                .IsRequired();
            builder.Property(command => command.IntentOperationalUnit)
                .HasColumnName("intent_operational_unit")
                .HasMaxLength(OperationalUnit.MaximumLength)
                .IsRequired();
            builder.Property(command => command.ResultItemId)
                .HasColumnName("result_item_id")
                .ValueGeneratedNever();
            builder.Property(command => command.ResultOperationalName)
                .HasColumnName("result_operational_name")
                .HasMaxLength(InventoryItem.OperationalNameMaximumLength)
                .IsRequired();
            builder.Property(command => command.ResultOperationalUnit)
                .HasColumnName("result_operational_unit")
                .HasMaxLength(OperationalUnit.MaximumLength)
                .IsRequired();
            builder.Property(command => command.ResultCurrentRegisteredQuantity)
                .HasColumnName("result_current_registered_quantity")
                .HasColumnType("numeric(28,12)");
            builder.Property(command => command.ResultMovementRevision)
                .HasColumnName("result_movement_revision")
                .HasColumnType("bigint")
                .IsRequired();
            builder.HasIndex(command => command.ResultItemId)
                .HasDatabaseName("UX_inventory_item_commands_result_item")
                .IsUnique();
            builder.HasOne<InventoryItem>()
                .WithMany()
                .HasForeignKey(command => command.ResultItemId)
                .HasConstraintName("FK_inventory_item_commands_item")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
