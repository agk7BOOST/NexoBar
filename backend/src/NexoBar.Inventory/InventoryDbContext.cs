using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.Inventory;

internal sealed class InventoryDbContext(
    DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    internal DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();

    internal DbSet<InventoryItemCreationCommand> InventoryItemCreationCommands =>
        Set<InventoryItemCreationCommand>();

    internal DbSet<CountObservation> CountObservations => Set<CountObservation>();

    internal DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();

    internal DbSet<InventoryCountCommand> InventoryCountCommands =>
        Set<InventoryCountCommand>();

    internal DbSet<InventoryMovementCommand> InventoryMovementCommands =>
        Set<InventoryMovementCommand>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("inventory");
        modelBuilder.ApplyConfiguration(new InventoryItemConfiguration());
        modelBuilder.ApplyConfiguration(new InventoryItemCreationCommandConfiguration());
        modelBuilder.ApplyConfiguration(new CountObservationConfiguration());
        modelBuilder.ApplyConfiguration(new InventoryMovementConfiguration());
        modelBuilder.ApplyConfiguration(new InventoryCountCommandConfiguration());
        modelBuilder.ApplyConfiguration(new InventoryMovementCommandConfiguration());
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

    private sealed class CountObservationConfiguration :
        IEntityTypeConfiguration<CountObservation>
    {
        public void Configure(EntityTypeBuilder<CountObservation> builder)
        {
            builder.ToTable(
                "count_observations",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_count_observations_quantity_non_negative",
                        "observed_quantity >= 0");
                    table.HasCheckConstraint(
                        "CK_count_observations_revision_non_negative",
                        "observed_movement_revision >= 0");
                    table.HasCheckConstraint(
                        "CK_count_observations_unit_valid",
                        "length(observed_operational_unit) BETWEEN 1 AND 100 AND " +
                        "observed_operational_unit = btrim(observed_operational_unit) AND " +
                        "position(chr(10) in observed_operational_unit) = 0 AND " +
                        "position(chr(13) in observed_operational_unit) = 0");
                });
            builder.HasKey(observation => observation.Id)
                .HasName("PK_count_observations");
            builder.Property(observation => observation.Id)
                .HasColumnName("id").ValueGeneratedNever();
            builder.Property(observation => observation.InventoryItemId)
                .HasColumnName("inventory_item_id").ValueGeneratedNever();
            builder.Property(observation => observation.ObservedQuantity)
                .HasColumnName("observed_quantity")
                .HasColumnType("numeric(28,12)").IsRequired();
            builder.Property(observation => observation.ObservedMovementRevision)
                .HasColumnName("observed_movement_revision")
                .HasColumnType("bigint").IsRequired();
            builder.Property(observation => observation.ObservedOperationalUnit)
                .HasColumnName("observed_operational_unit")
                .HasMaxLength(OperationalUnit.MaximumLength).IsRequired();
            builder.Property(observation => observation.ObservedAt)
                .HasColumnName("observed_at")
                .HasColumnType("timestamp with time zone").IsRequired();
            builder.Property(observation => observation.ActorIdentityId)
                .HasColumnName("actor_identity_id").ValueGeneratedNever();
            builder.HasIndex(observation => new
            {
                observation.InventoryItemId,
                observation.ObservedAt
            })
                .HasDatabaseName("IX_count_observations_item_observed_at");
            builder.HasOne<InventoryItem>().WithMany()
                .HasForeignKey(observation => observation.InventoryItemId)
                .HasConstraintName("FK_count_observations_item")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class InventoryMovementConfiguration :
        IEntityTypeConfiguration<InventoryMovement>
    {
        public void Configure(EntityTypeBuilder<InventoryMovement> builder)
        {
            builder.ToTable(
                "inventory_movements",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_inventory_movements_revision_positive",
                        "movement_revision > 0");
                    table.HasCheckConstraint(
                        "CK_inventory_movements_nature",
                        "nature IN ('Reconciliation', 'Entry', 'ManualExit', 'Waste', 'Correction')");
                    table.HasCheckConstraint(
                        "CK_inventory_movements_reconciliation_count",
                        "(nature = 'Reconciliation' AND count_observation_id IS NOT NULL) OR " +
                        "(nature <> 'Reconciliation' AND count_observation_id IS NULL)");
                    table.HasCheckConstraint(
                        "CK_inventory_movements_reconciliation_quantity",
                        "(nature = 'Reconciliation' AND quantity >= 0) OR " +
                        "(nature IN ('Entry', 'ManualExit', 'Waste') AND quantity > 0) OR " +
                        "nature = 'Correction'");
                });
            builder.HasKey(movement => movement.Id)
                .HasName("PK_inventory_movements");
            builder.Property(movement => movement.Id)
                .HasColumnName("id").ValueGeneratedNever();
            builder.Property(movement => movement.InventoryItemId)
                .HasColumnName("inventory_item_id").ValueGeneratedNever();
            builder.Property(movement => movement.MovementRevision)
                .HasColumnName("movement_revision").HasColumnType("bigint").IsRequired();
            builder.Property(movement => movement.Nature)
                .HasColumnName("nature").HasMaxLength(32).IsRequired();
            builder.Property(movement => movement.Quantity)
                .HasColumnName("quantity").HasColumnType("numeric(28,12)").IsRequired();
            builder.Property(movement => movement.PreviousRegisteredQuantity)
                .HasColumnName("previous_registered_quantity")
                .HasColumnType("numeric(28,12)");
            builder.Property(movement => movement.ResultingRegisteredQuantity)
                .HasColumnName("resulting_registered_quantity")
                .HasColumnType("numeric(28,12)").IsRequired();
            builder.Property(movement => movement.CountObservationId)
                .HasColumnName("count_observation_id").ValueGeneratedNever();
            builder.Property(movement => movement.OccurredAt)
                .HasColumnName("occurred_at")
                .HasColumnType("timestamp with time zone").IsRequired();
            builder.Property(movement => movement.ActorIdentityId)
                .HasColumnName("actor_identity_id").ValueGeneratedNever();
            builder.HasIndex(movement => new
            {
                movement.InventoryItemId,
                movement.MovementRevision
            })
                .HasDatabaseName("UX_inventory_movements_item_revision")
                .IsUnique();
            builder.HasIndex(movement => movement.CountObservationId)
                .HasDatabaseName("UX_inventory_movements_count_observation")
                .HasFilter("count_observation_id IS NOT NULL")
                .IsUnique();
            builder.HasOne<InventoryItem>().WithMany()
                .HasForeignKey(movement => movement.InventoryItemId)
                .HasConstraintName("FK_inventory_movements_item")
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<CountObservation>().WithMany()
                .HasForeignKey(movement => movement.CountObservationId)
                .HasConstraintName("FK_inventory_movements_count_observation")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class InventoryCountCommandConfiguration :
        IEntityTypeConfiguration<InventoryCountCommand>
    {
        public void Configure(EntityTypeBuilder<InventoryCountCommand> builder)
        {
            builder.ToTable(
                "count_commands",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_count_commands_kind",
                        "command_kind = 'RecordInventoryCount'");
                    table.HasCheckConstraint(
                        "CK_count_commands_quantity_non_negative",
                        "observed_quantity >= 0");
                    table.HasCheckConstraint(
                        "CK_count_commands_revision_non_negative",
                        "result_observed_movement_revision >= 0");
                });
            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_count_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id").ValueGeneratedNever();
            builder.Property(command => command.CommandKind)
                .HasColumnName("command_kind").HasMaxLength(40).IsRequired();
            builder.Property(command => command.InventoryItemId)
                .HasColumnName("inventory_item_id").ValueGeneratedNever();
            builder.Property(command => command.ObservedQuantity)
                .HasColumnName("observed_quantity")
                .HasColumnType("numeric(28,12)").IsRequired();
            builder.Property(command => command.ResultCountObservationId)
                .HasColumnName("result_count_observation_id").ValueGeneratedNever();
            builder.Property(command => command.ResultObservedMovementRevision)
                .HasColumnName("result_observed_movement_revision")
                .HasColumnType("bigint").IsRequired();
            builder.Property(command => command.ResultObservedOperationalUnit)
                .HasColumnName("result_observed_operational_unit")
                .HasMaxLength(OperationalUnit.MaximumLength).IsRequired();
            builder.Property(command => command.ResultObservedAt)
                .HasColumnName("result_observed_at")
                .HasColumnType("timestamp with time zone").IsRequired();
            builder.HasIndex(command => command.ResultCountObservationId)
                .HasDatabaseName("UX_count_commands_result_observation").IsUnique();
            builder.HasOne<CountObservation>().WithMany()
                .HasForeignKey(command => command.ResultCountObservationId)
                .HasConstraintName("FK_count_commands_observation")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class InventoryMovementCommandConfiguration :
        IEntityTypeConfiguration<InventoryMovementCommand>
    {
        public void Configure(EntityTypeBuilder<InventoryMovementCommand> builder)
        {
            builder.ToTable(
                "movement_commands",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_movement_commands_kind",
                        "command_kind IN ('ReconcileInventoryCount', 'RecordInventoryEntry', " +
                        "'RecordManualInventoryExit', 'RecordInventoryWaste')");
                    table.HasCheckConstraint(
                        "CK_movement_commands_outcome",
                        "result_outcome IS NULL OR " +
                        "result_outcome IN ('reconciled', 'no_discrepancy')");
                    table.HasCheckConstraint(
                        "CK_movement_commands_revision_non_negative",
                        "result_movement_revision >= 0");
                    table.HasCheckConstraint(
                        "CK_movement_commands_result_shape",
                        "(command_kind = 'ReconcileInventoryCount' AND count_observation_id IS NOT NULL AND " +
                        "intent_quantity IS NULL AND result_observed_quantity IS NOT NULL AND " +
                        "((result_outcome = 'reconciled' AND result_movement_id IS NOT NULL AND result_occurred_at IS NOT NULL) OR " +
                        "(result_outcome = 'no_discrepancy' AND result_movement_id IS NULL AND result_occurred_at IS NULL AND " +
                        "result_previous_registered_quantity = result_observed_quantity AND " +
                        "result_observed_quantity = result_resulting_registered_quantity))) OR " +
                        "(command_kind IN ('RecordInventoryEntry', 'RecordManualInventoryExit', 'RecordInventoryWaste') AND " +
                        "count_observation_id IS NULL AND intent_quantity > 0 AND result_outcome IS NULL AND " +
                        "result_observed_quantity IS NULL AND result_movement_id IS NOT NULL AND " +
                        "result_occurred_at IS NOT NULL AND result_previous_registered_quantity IS NOT NULL AND " +
                        "result_movement_revision > 0)");
                });
            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_movement_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id").ValueGeneratedNever();
            builder.Property(command => command.CommandKind)
                .HasColumnName("command_kind").HasMaxLength(40).IsRequired();
            builder.Property(command => command.InventoryItemId)
                .HasColumnName("inventory_item_id").ValueGeneratedNever();
            builder.Property(command => command.CountObservationId)
                .HasColumnName("count_observation_id").ValueGeneratedNever();
            builder.Property(command => command.IntentQuantity)
                .HasColumnName("intent_quantity")
                .HasColumnType("numeric(28,12)");
            builder.Property(command => command.ResultOutcome)
                .HasColumnName("result_outcome").HasMaxLength(24);
            builder.Property(command => command.ResultMovementId)
                .HasColumnName("result_movement_id").ValueGeneratedNever();
            builder.Property(command => command.ResultOccurredAt)
                .HasColumnName("result_occurred_at")
                .HasColumnType("timestamp with time zone");
            builder.Property(command => command.ResultPreviousRegisteredQuantity)
                .HasColumnName("result_previous_registered_quantity")
                .HasColumnType("numeric(28,12)");
            builder.Property(command => command.ResultObservedQuantity)
                .HasColumnName("result_observed_quantity")
                .HasColumnType("numeric(28,12)");
            builder.Property(command => command.ResultResultingRegisteredQuantity)
                .HasColumnName("result_resulting_registered_quantity")
                .HasColumnType("numeric(28,12)").IsRequired();
            builder.Property(command => command.ResultMovementRevision)
                .HasColumnName("result_movement_revision")
                .HasColumnType("bigint").IsRequired();
            builder.HasIndex(command => command.ResultMovementId)
                .HasDatabaseName("UX_movement_commands_result_movement")
                .HasFilter("result_movement_id IS NOT NULL").IsUnique();
            builder.HasOne<InventoryItem>().WithMany()
                .HasForeignKey(command => command.InventoryItemId)
                .HasConstraintName("FK_movement_commands_item")
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<CountObservation>().WithMany()
                .HasForeignKey(command => command.CountObservationId)
                .HasConstraintName("FK_movement_commands_observation")
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<InventoryMovement>().WithMany()
                .HasForeignKey(command => command.ResultMovementId)
                .HasConstraintName("FK_movement_commands_movement")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
