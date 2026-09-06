using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.OrderOperations;

internal sealed class OrderOperationsDbContext(
    DbContextOptions<OrderOperationsDbContext> options) : DbContext(options)
{
    internal DbSet<Order> Orders => Set<Order>();
    internal DbSet<Closure> Closures => Set<Closure>();
    internal DbSet<ClosureHistory> ClosureHistory => Set<ClosureHistory>();
    internal DbSet<ClosureCommand> ClosureCommands => Set<ClosureCommand>();
    internal DbSet<Liquidation> Liquidations => Set<Liquidation>();
    internal DbSet<LiquidationHistory> LiquidationHistory => Set<LiquidationHistory>();
    internal DbSet<LiquidationCommand> LiquidationCommands => Set<LiquidationCommand>();
    internal DbSet<PendingComposition> PendingCompositions => Set<PendingComposition>();
    internal DbSet<PendingCompositionCommand> PendingCompositionCommands =>
        Set<PendingCompositionCommand>();
    internal DbSet<Incorporation> Incorporations => Set<Incorporation>();
    internal DbSet<IncorporationContent> IncorporationContents => Set<IncorporationContent>();
    internal DbSet<ContentQuantityState> ContentQuantityStates => Set<ContentQuantityState>();
    internal DbSet<DeliveryState> DeliveryStates => Set<DeliveryState>();
    internal DbSet<DeliveryHistory> DeliveryHistory => Set<DeliveryHistory>();
    internal DbSet<DeliveryCommand> DeliveryCommands => Set<DeliveryCommand>();
    internal DbSet<ContentCorrectionHistory> ContentCorrectionHistory => Set<ContentCorrectionHistory>();
    internal DbSet<ContentCorrectionCommand> ContentCorrectionCommands => Set<ContentCorrectionCommand>();
    internal DbSet<DeliveryCorrectionHistory> DeliveryCorrectionHistory => Set<DeliveryCorrectionHistory>();
    internal DbSet<DeliveryCorrectionCommand> DeliveryCorrectionCommands => Set<DeliveryCorrectionCommand>();
    internal DbSet<PreparationWork> PreparationWork => Set<PreparationWork>();
    internal DbSet<PreparationHistory> PreparationHistory => Set<PreparationHistory>();
    internal DbSet<PreparationCommand> PreparationCommands => Set<PreparationCommand>();
    internal DbSet<ConfirmationHistory> ConfirmationHistory => Set<ConfirmationHistory>();
    internal DbSet<FirstConfirmationCommand> FirstConfirmationCommands =>
        Set<FirstConfirmationCommand>();
    internal DbSet<FirstConfirmationCommandContent> FirstConfirmationCommandContents =>
        Set<FirstConfirmationCommandContent>();
    internal DbSet<SubsequentConfirmationCommand> SubsequentConfirmationCommands =>
        Set<SubsequentConfirmationCommand>();
    internal DbSet<SubsequentConfirmationCommandContent>
        SubsequentConfirmationCommandContents => Set<SubsequentConfirmationCommandContent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("order_operations");
        modelBuilder.ApplyConfiguration(new OrderConfiguration());
        modelBuilder.ApplyConfiguration(new ClosureConfiguration());
        modelBuilder.ApplyConfiguration(new ClosureHistoryConfiguration());
        modelBuilder.ApplyConfiguration(new ClosureCommandConfiguration());
        modelBuilder.ApplyConfiguration(new LiquidationConfiguration());
        modelBuilder.ApplyConfiguration(new LiquidationHistoryConfiguration());
        modelBuilder.ApplyConfiguration(new LiquidationCommandConfiguration());
        modelBuilder.ApplyConfiguration(new PendingCompositionConfiguration());
        modelBuilder.ApplyConfiguration(new PendingCompositionCommandConfiguration());
        modelBuilder.ApplyConfiguration(new IncorporationConfiguration());
        modelBuilder.ApplyConfiguration(new IncorporationContentConfiguration());
        modelBuilder.ApplyConfiguration(new ContentQuantityStateConfiguration());
        modelBuilder.ApplyConfiguration(new DeliveryStateConfiguration());
        modelBuilder.ApplyConfiguration(new DeliveryHistoryConfiguration());
        modelBuilder.ApplyConfiguration(new DeliveryCommandConfiguration());
        modelBuilder.ApplyConfiguration(new ContentCorrectionHistoryConfiguration());
        modelBuilder.ApplyConfiguration(new ContentCorrectionCommandConfiguration());
        modelBuilder.ApplyConfiguration(new DeliveryCorrectionHistoryConfiguration());
        modelBuilder.ApplyConfiguration(new DeliveryCorrectionCommandConfiguration());
        modelBuilder.ApplyConfiguration(new PreparationWorkConfiguration());
        modelBuilder.ApplyConfiguration(new PreparationHistoryConfiguration());
        modelBuilder.ApplyConfiguration(new PreparationCommandConfiguration());
        modelBuilder.ApplyConfiguration(new ConfirmationHistoryConfiguration());
        modelBuilder.ApplyConfiguration(new FirstConfirmationCommandConfiguration());
        modelBuilder.ApplyConfiguration(new FirstConfirmationCommandContentConfiguration());
        modelBuilder.ApplyConfiguration(new SubsequentConfirmationCommandConfiguration());
        modelBuilder.ApplyConfiguration(new SubsequentConfirmationCommandContentConfiguration());
    }

    private sealed class DeliveryStateConfiguration :
        IEntityTypeConfiguration<DeliveryState>
    {
        public void Configure(EntityTypeBuilder<DeliveryState> builder)
        {
            builder.ToTable(
                "delivery_states",
                table => table.HasCheckConstraint(
                    "CK_order_operations_delivery_states_delivered_non_negative",
                    "delivered_quantity >= 0"));
            builder.HasKey(state => new
            {
                state.IncorporationId,
                state.ContentOrdinal
            })
                .HasName("PK_order_operations_delivery_states");
            builder.Property(state => state.IncorporationId)
                .HasColumnName("incorporation_id").ValueGeneratedNever();
            builder.Property(state => state.ContentOrdinal)
                .HasColumnName("content_ordinal").ValueGeneratedNever();
            builder.Property(state => state.DeliveredQuantity)
                .HasColumnName("delivered_quantity").IsRequired();
            builder.HasOne<IncorporationContent>().WithOne()
                .HasForeignKey<DeliveryState>(state => new
                {
                    state.IncorporationId,
                    state.ContentOrdinal
                })
                .HasConstraintName("FK_order_operations_delivery_states_content")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class ContentQuantityStateConfiguration :
        IEntityTypeConfiguration<ContentQuantityState>
    {
        public void Configure(EntityTypeBuilder<ContentQuantityState> builder)
        {
            builder.ToTable(
                "content_quantity_states",
                table => table.HasCheckConstraint(
                    "CK_content_quantity_states_removed_non_negative",
                    "removed_by_correction_quantity >= 0"));
            builder.HasKey(state => new { state.IncorporationId, state.ContentOrdinal })
                .HasName("PK_content_quantity_states");
            builder.Property(state => state.IncorporationId)
                .HasColumnName("incorporation_id").ValueGeneratedNever();
            builder.Property(state => state.ContentOrdinal)
                .HasColumnName("content_ordinal").ValueGeneratedNever();
            builder.Property(state => state.RemovedByCorrectionQuantity)
                .HasColumnName("removed_by_correction_quantity").IsRequired();
            builder.HasOne<IncorporationContent>().WithOne()
                .HasForeignKey<ContentQuantityState>(state => new
                {
                    state.IncorporationId,
                    state.ContentOrdinal
                })
                .HasConstraintName("FK_content_quantity_states_content")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class DeliveryHistoryConfiguration :
        IEntityTypeConfiguration<DeliveryHistory>
    {
        public void Configure(EntityTypeBuilder<DeliveryHistory> builder)
        {
            builder.ToTable(
                "delivery_history",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_delivery_history_event_kind_not_empty",
                        "length(btrim(event_kind)) > 0");
                    table.HasCheckConstraint(
                        "CK_delivery_history_content_ordinal_positive",
                        "content_ordinal > 0");
                    table.HasCheckConstraint(
                        "CK_delivery_history_quantity_positive",
                        "quantity > 0");
                    table.HasCheckConstraint(
                        "CK_delivery_history_result_non_negative",
                        "resulting_delivered_quantity >= 0");
                });
            builder.HasKey(history => history.Id)
                .HasName("PK_order_operations_delivery_history");
            builder.Property(history => history.Id)
                .HasColumnName("id").ValueGeneratedNever();
            builder.Property(history => history.IncorporationId)
                .HasColumnName("incorporation_id").ValueGeneratedNever();
            builder.Property(history => history.ContentOrdinal)
                .HasColumnName("content_ordinal").ValueGeneratedNever();
            builder.Property(history => history.EventKind)
                .HasColumnName("event_kind").HasColumnType("text").IsRequired();
            builder.Property(history => history.Quantity)
                .HasColumnName("quantity").IsRequired();
            builder.Property(history => history.ActorIdentityId)
                .HasColumnName("actor_identity_id").ValueGeneratedNever();
            builder.Property(history => history.OccurredAt)
                .HasColumnName("occurred_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.Property(history => history.ResultingDeliveredQuantity)
                .HasColumnName("resulting_delivered_quantity").IsRequired();
            builder.HasIndex(history => new
            {
                history.IncorporationId,
                history.ContentOrdinal,
                history.OccurredAt,
                history.Id
            })
                .HasDatabaseName("IX_delivery_history_content_time_id");
            builder.HasOne<IncorporationContent>().WithMany()
                .HasForeignKey(history => new
                {
                    history.IncorporationId,
                    history.ContentOrdinal
                })
                .HasConstraintName("FK_delivery_history_content")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class DeliveryCommandConfiguration :
        IEntityTypeConfiguration<DeliveryCommand>
    {
        public void Configure(EntityTypeBuilder<DeliveryCommand> builder)
        {
            builder.ToTable(
                "delivery_commands",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_delivery_commands_kind_not_empty",
                        "length(btrim(command_kind)) > 0");
                    table.HasCheckConstraint(
                        "CK_delivery_commands_content_ordinal_positive",
                        "content_ordinal > 0");
                    table.HasCheckConstraint(
                        "CK_delivery_commands_quantity_positive",
                        "quantity > 0");
                    table.HasCheckConstraint(
                        "CK_delivery_commands_result_non_negative",
                        "result_delivered_quantity >= 0");
                });
            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_order_operations_delivery_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id").ValueGeneratedNever();
            builder.Property(command => command.CommandKind)
                .HasColumnName("command_kind").HasColumnType("text").IsRequired();
            builder.Property(command => command.IncorporationId)
                .HasColumnName("incorporation_id").ValueGeneratedNever();
            builder.Property(command => command.ContentOrdinal)
                .HasColumnName("content_ordinal").ValueGeneratedNever();
            builder.Property(command => command.Quantity)
                .HasColumnName("quantity").IsRequired();
            builder.Property(command => command.ResultHistoryId)
                .HasColumnName("result_history_id").ValueGeneratedNever();
            builder.Property(command => command.ResultOccurredAt)
                .HasColumnName("result_occurred_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.Property(command => command.ResultDeliveredQuantity)
                .HasColumnName("result_delivered_quantity").IsRequired();
            builder.HasIndex(command => new
            {
                command.IncorporationId,
                command.ContentOrdinal
            })
                .HasDatabaseName("IX_delivery_commands_content");
            builder.HasIndex(command => command.ResultHistoryId)
                .HasDatabaseName("UX_delivery_commands_history")
                .IsUnique();
            builder.HasOne<IncorporationContent>().WithMany()
                .HasForeignKey(command => new
                {
                    command.IncorporationId,
                    command.ContentOrdinal
                })
                .HasConstraintName("FK_delivery_commands_content")
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<DeliveryHistory>().WithOne()
                .HasForeignKey<DeliveryCommand>(command => command.ResultHistoryId)
                .HasConstraintName("FK_delivery_commands_history")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class PreparationWorkConfiguration :
        IEntityTypeConfiguration<PreparationWork>
    {
        public void Configure(EntityTypeBuilder<PreparationWork> builder)
        {
            builder.ToTable(
                "preparation_work",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_order_operations_preparation_work_total_positive",
                        "total_quantity >= 0");
                    table.HasCheckConstraint(
                        "CK_order_operations_preparation_work_quantities_non_negative",
                        "pending_quantity >= 0 AND " +
                        "in_preparation_quantity >= 0 AND ready_quantity >= 0");
                    table.HasCheckConstraint(
                        "CK_order_operations_preparation_work_quantities_balanced",
                        "pending_quantity::bigint + " +
                        "in_preparation_quantity::bigint + " +
                        "ready_quantity::bigint = total_quantity::bigint");
                });
            builder.HasKey(work => work.Id)
                .HasName("PK_order_operations_preparation_work");
            builder.Property(work => work.Id)
                .HasColumnName("id").ValueGeneratedNever();
            builder.Property(work => work.IncorporationId)
                .HasColumnName("incorporation_id").ValueGeneratedNever();
            builder.Property(work => work.ContentOrdinal)
                .HasColumnName("content_ordinal").ValueGeneratedNever();
            builder.Property(work => work.PreparationResponsibilityId)
                .HasColumnName("preparation_responsibility_id").ValueGeneratedNever();
            builder.Property(work => work.TotalQuantity)
                .HasColumnName("total_quantity").IsRequired();
            builder.Property(work => work.PendingQuantity)
                .HasColumnName("pending_quantity").IsRequired();
            builder.Property(work => work.InPreparationQuantity)
                .HasColumnName("in_preparation_quantity").IsRequired();
            builder.Property(work => work.ReadyQuantity)
                .HasColumnName("ready_quantity").IsRequired();
            builder.HasIndex(work => new { work.IncorporationId, work.ContentOrdinal })
                .HasDatabaseName("UX_order_operations_preparation_work_content")
                .IsUnique();
            builder.HasIndex(work => work.PreparationResponsibilityId)
                .HasDatabaseName(
                    "IX_order_operations_preparation_work_responsibility");
            builder.HasOne<IncorporationContent>().WithOne()
                .HasForeignKey<PreparationWork>(
                    work => new { work.IncorporationId, work.ContentOrdinal })
                .HasConstraintName("FK_order_operations_preparation_work_content")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class PreparationHistoryConfiguration :
        IEntityTypeConfiguration<PreparationHistory>
    {
        public void Configure(EntityTypeBuilder<PreparationHistory> builder)
        {
            builder.ToTable(
                "preparation_history",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_preparation_history_event_kind_not_empty",
                        "length(btrim(event_kind)) > 0");
                    table.HasCheckConstraint(
                        "CK_preparation_history_quantity_positive",
                        "quantity > 0");
                    table.HasCheckConstraint(
                        "CK_preparation_history_result_total_positive",
                        "resulting_total_quantity > 0");
                    table.HasCheckConstraint(
                        "CK_preparation_history_result_non_negative",
                        "resulting_pending_quantity >= 0 AND " +
                        "resulting_in_preparation_quantity >= 0 AND " +
                        "resulting_ready_quantity >= 0");
                    table.HasCheckConstraint(
                        "CK_preparation_history_result_balanced",
                        "resulting_pending_quantity::bigint + " +
                        "resulting_in_preparation_quantity::bigint + " +
                        "resulting_ready_quantity::bigint = " +
                        "resulting_total_quantity::bigint");
                });
            builder.HasKey(history => history.Id)
                .HasName("PK_order_operations_preparation_history");
            builder.Property(history => history.Id)
                .HasColumnName("id").ValueGeneratedNever();
            builder.Property(history => history.WorkId)
                .HasColumnName("work_id").ValueGeneratedNever();
            builder.Property(history => history.EventKind)
                .HasColumnName("event_kind").HasColumnType("text").IsRequired();
            builder.Property(history => history.Quantity)
                .HasColumnName("quantity").IsRequired();
            builder.Property(history => history.ActorIdentityId)
                .HasColumnName("actor_identity_id").ValueGeneratedNever();
            builder.Property(history => history.OccurredAt)
                .HasColumnName("occurred_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.Property(history => history.ResultingTotalQuantity)
                .HasColumnName("resulting_total_quantity").IsRequired();
            builder.Property(history => history.ResultingPendingQuantity)
                .HasColumnName("resulting_pending_quantity").IsRequired();
            builder.Property(history => history.ResultingInPreparationQuantity)
                .HasColumnName("resulting_in_preparation_quantity").IsRequired();
            builder.Property(history => history.ResultingReadyQuantity)
                .HasColumnName("resulting_ready_quantity").IsRequired();
            builder.HasIndex(history => new
            {
                history.WorkId,
                history.OccurredAt,
                history.Id
            })
                .HasDatabaseName(
                    "IX_order_operations_preparation_history_work_time_id");
            builder.HasOne<PreparationWork>().WithMany()
                .HasForeignKey(history => history.WorkId)
                .HasConstraintName(
                    "FK_order_operations_preparation_history_work")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class PreparationCommandConfiguration :
        IEntityTypeConfiguration<PreparationCommand>
    {
        public void Configure(EntityTypeBuilder<PreparationCommand> builder)
        {
            builder.ToTable(
                "preparation_commands",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_preparation_commands_kind_not_empty",
                        "length(btrim(command_kind)) > 0");
                    table.HasCheckConstraint(
                        "CK_preparation_commands_quantity_positive",
                        "quantity > 0");
                    table.HasCheckConstraint(
                        "CK_preparation_commands_result_total_positive",
                        "result_total_quantity > 0");
                    table.HasCheckConstraint(
                        "CK_preparation_commands_result_non_negative",
                        "result_pending_quantity >= 0 AND " +
                        "result_in_preparation_quantity >= 0 AND " +
                        "result_ready_quantity >= 0");
                    table.HasCheckConstraint(
                        "CK_preparation_commands_result_balanced",
                        "result_pending_quantity::bigint + " +
                        "result_in_preparation_quantity::bigint + " +
                        "result_ready_quantity::bigint = " +
                        "result_total_quantity::bigint");
                });
            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_order_operations_preparation_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id").ValueGeneratedNever();
            builder.Property(command => command.CommandKind)
                .HasColumnName("command_kind").HasColumnType("text").IsRequired();
            builder.Property(command => command.WorkId)
                .HasColumnName("work_id").ValueGeneratedNever();
            builder.Property(command => command.Quantity)
                .HasColumnName("quantity").IsRequired();
            builder.Property(command => command.ResultHistoryId)
                .HasColumnName("result_history_id").ValueGeneratedNever();
            builder.Property(command => command.ResultOccurredAt)
                .HasColumnName("result_occurred_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.Property(command => command.ResultTotalQuantity)
                .HasColumnName("result_total_quantity").IsRequired();
            builder.Property(command => command.ResultPendingQuantity)
                .HasColumnName("result_pending_quantity").IsRequired();
            builder.Property(command => command.ResultInPreparationQuantity)
                .HasColumnName("result_in_preparation_quantity").IsRequired();
            builder.Property(command => command.ResultReadyQuantity)
                .HasColumnName("result_ready_quantity").IsRequired();
            builder.HasIndex(command => command.WorkId)
                .HasDatabaseName(
                    "IX_order_operations_preparation_commands_work");
            builder.HasIndex(command => command.ResultHistoryId)
                .HasDatabaseName(
                    "UX_order_operations_preparation_commands_history")
                .IsUnique();
            builder.HasOne<PreparationWork>().WithMany()
                .HasForeignKey(command => command.WorkId)
                .HasConstraintName(
                    "FK_order_operations_preparation_commands_work")
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<PreparationHistory>().WithOne()
                .HasForeignKey<PreparationCommand>(
                    command => command.ResultHistoryId)
                .HasConstraintName(
                    "FK_order_operations_preparation_commands_history")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
    {
        public void Configure(EntityTypeBuilder<Order> builder)
        {
            builder.ToTable(
                "orders",
                table => table.HasCheckConstraint(
                    "CK_order_operations_orders_context_not_empty",
                    "length(btrim(context)) > 0"));
            builder.HasKey(order => order.Id).HasName("PK_order_operations_orders");
            builder.Property(order => order.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(order => order.Context)
                .HasColumnName("context").HasColumnType("text").IsRequired();
        }
    }

    private sealed class LiquidationConfiguration : IEntityTypeConfiguration<Liquidation>
    {
        public void Configure(EntityTypeBuilder<Liquidation> builder)
        {
            builder.ToTable(
                "liquidations",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_liquidations_mode",
                        "mode IN ('Simple', 'ExternalCollection')");
                    table.HasCheckConstraint(
                        "CK_liquidations_amount_non_negative",
                        "functional_amount >= 0");
                    table.HasCheckConstraint(
                        "CK_liquidations_medium_shape",
                        "(mode = 'Simple' AND declared_payment_medium IS NOT NULL AND " +
                        "length(declared_payment_medium) BETWEEN 1 AND 200) OR " +
                        "(mode = 'ExternalCollection' AND declared_payment_medium IS NULL)");
                });
            builder.HasKey(liquidation => liquidation.Id)
                .HasName("PK_order_operations_liquidations");
            builder.Property(liquidation => liquidation.Id)
                .HasColumnName("id").ValueGeneratedNever();
            builder.Property(liquidation => liquidation.OrderId)
                .HasColumnName("order_id").ValueGeneratedNever();
            builder.Property(liquidation => liquidation.Mode)
                .HasColumnName("mode").HasColumnType("text").IsRequired();
            builder.Property(liquidation => liquidation.FunctionalAmount)
                .HasColumnName("functional_amount").HasColumnType("numeric").IsRequired();
            builder.Property(liquidation => liquidation.DeclaredPaymentMedium)
                .HasColumnName("declared_payment_medium")
                .HasMaxLength(200);
            builder.Property(liquidation => liquidation.OccurredAt)
                .HasColumnName("occurred_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.Property(liquidation => liquidation.ActorIdentityId)
                .HasColumnName("actor_identity_id").ValueGeneratedNever();
            builder.HasIndex(liquidation => liquidation.OrderId)
                .HasDatabaseName("UX_liquidations_order")
                .IsUnique();
            builder.HasOne<Order>().WithOne()
                .HasForeignKey<Liquidation>(liquidation => liquidation.OrderId)
                .HasConstraintName("FK_liquidations_orders")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class LiquidationHistoryConfiguration :
        IEntityTypeConfiguration<LiquidationHistory>
    {
        public void Configure(EntityTypeBuilder<LiquidationHistory> builder)
        {
            builder.ToTable(
                "liquidation_history",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_liquidation_history_event_kind",
                        "event_kind = 'Liquidated'");
                    table.HasCheckConstraint(
                        "CK_liquidation_history_mode",
                        "mode IN ('Simple', 'ExternalCollection')");
                    table.HasCheckConstraint(
                        "CK_liquidation_history_amount_non_negative",
                        "functional_amount >= 0");
                    table.HasCheckConstraint(
                        "CK_liquidation_history_medium_shape",
                        "(mode = 'Simple' AND declared_payment_medium IS NOT NULL AND " +
                        "length(declared_payment_medium) BETWEEN 1 AND 200) OR " +
                        "(mode = 'ExternalCollection' AND declared_payment_medium IS NULL)");
                });
            builder.HasKey(history => history.Id)
                .HasName("PK_order_operations_liquidation_history");
            builder.Property(history => history.Id)
                .HasColumnName("id").ValueGeneratedNever();
            builder.Property(history => history.LiquidationId)
                .HasColumnName("liquidation_id").ValueGeneratedNever();
            builder.Property(history => history.OrderId)
                .HasColumnName("order_id").ValueGeneratedNever();
            builder.Property(history => history.EventKind)
                .HasColumnName("event_kind").HasColumnType("text").IsRequired();
            builder.Property(history => history.Mode)
                .HasColumnName("mode").HasColumnType("text").IsRequired();
            builder.Property(history => history.FunctionalAmount)
                .HasColumnName("functional_amount").HasColumnType("numeric").IsRequired();
            builder.Property(history => history.DeclaredPaymentMedium)
                .HasColumnName("declared_payment_medium")
                .HasMaxLength(200);
            builder.Property(history => history.OccurredAt)
                .HasColumnName("occurred_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.Property(history => history.ActorIdentityId)
                .HasColumnName("actor_identity_id").ValueGeneratedNever();
            builder.HasIndex(history => history.LiquidationId)
                .HasDatabaseName("UX_liquidation_history_liquidation")
                .IsUnique();
            builder.HasIndex(history => new { history.OrderId, history.OccurredAt, history.Id })
                .HasDatabaseName("IX_liquidation_history_order_time_id");
            builder.HasOne<Liquidation>().WithOne()
                .HasForeignKey<LiquidationHistory>(history => history.LiquidationId)
                .HasConstraintName("FK_liquidation_history_liquidations")
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<Order>().WithMany()
                .HasForeignKey(history => history.OrderId)
                .HasConstraintName("FK_liquidation_history_orders")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class LiquidationCommandConfiguration :
        IEntityTypeConfiguration<LiquidationCommand>
    {
        public void Configure(EntityTypeBuilder<LiquidationCommand> builder)
        {
            builder.ToTable(
                "liquidation_commands",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_liquidation_commands_kind",
                        "command_kind IN ('LiquidateSimple', 'RecordExternalCollection')");
                    table.HasCheckConstraint(
                        "CK_liquidation_commands_intent_medium",
                        "(command_kind = 'LiquidateSimple' AND declared_payment_medium IS NOT NULL " +
                        "AND length(declared_payment_medium) BETWEEN 1 AND 200) OR " +
                        "(command_kind = 'RecordExternalCollection' AND " +
                        "declared_payment_medium IS NULL)");
                    table.HasCheckConstraint(
                        "CK_liquidation_commands_result_amount_non_negative",
                        "result_functional_amount >= 0");
                });
            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_order_operations_liquidation_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id").ValueGeneratedNever();
            builder.Property(command => command.CommandKind)
                .HasColumnName("command_kind").HasColumnType("text").IsRequired();
            builder.Property(command => command.OrderId)
                .HasColumnName("order_id").ValueGeneratedNever();
            builder.Property(command => command.DeclaredPaymentMedium)
                .HasColumnName("declared_payment_medium").HasMaxLength(200);
            builder.Property(command => command.ResultLiquidationId)
                .HasColumnName("result_liquidation_id").ValueGeneratedNever();
            builder.Property(command => command.ResultMode)
                .HasColumnName("result_mode").HasColumnType("text").IsRequired();
            builder.Property(command => command.ResultFunctionalAmount)
                .HasColumnName("result_functional_amount")
                .HasColumnType("numeric").IsRequired();
            builder.Property(command => command.ResultDeclaredPaymentMedium)
                .HasColumnName("result_declared_payment_medium").HasMaxLength(200);
            builder.Property(command => command.ResultOccurredAt)
                .HasColumnName("result_occurred_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.HasIndex(command => command.OrderId)
                .HasDatabaseName("IX_liquidation_commands_order");
            builder.HasIndex(command => command.ResultLiquidationId)
                .HasDatabaseName("UX_liquidation_commands_liquidation")
                .IsUnique();
            builder.HasOne<Order>().WithMany()
                .HasForeignKey(command => command.OrderId)
                .HasConstraintName("FK_liquidation_commands_orders")
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<Liquidation>().WithOne()
                .HasForeignKey<LiquidationCommand>(command => command.ResultLiquidationId)
                .HasConstraintName("FK_liquidation_commands_liquidations")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class PendingCompositionConfiguration :
        IEntityTypeConfiguration<PendingComposition>
    {
        public void Configure(EntityTypeBuilder<PendingComposition> builder)
        {
            builder.ToTable("pending_compositions");
            builder.HasKey(pending => pending.Id)
                .HasName("PK_order_operations_pending_compositions");
            builder.Property(pending => pending.Id)
                .HasColumnName("id").ValueGeneratedNever();
            builder.Property(pending => pending.OrderId)
                .HasColumnName("order_id").ValueGeneratedNever();
            builder.Property(pending => pending.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.Property(pending => pending.CreatedByIdentityId)
                .HasColumnName("created_by_identity_id")
                .ValueGeneratedNever();
            builder.HasIndex(pending => pending.OrderId)
                .HasDatabaseName("UX_pending_compositions_order")
                .IsUnique();
            builder.HasOne<Order>().WithMany()
                .HasForeignKey(pending => pending.OrderId)
                .HasConstraintName("FK_pending_compositions_orders")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class PendingCompositionCommandConfiguration :
        IEntityTypeConfiguration<PendingCompositionCommand>
    {
        public void Configure(EntityTypeBuilder<PendingCompositionCommand> builder)
        {
            builder.ToTable(
                "pending_composition_commands",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_pending_composition_commands_kind",
                        "command_kind IN ('StartPendingComposition', 'DiscardPendingComposition')");
                    table.HasCheckConstraint(
                        "CK_pending_composition_commands_intent",
                        "(command_kind = 'StartPendingComposition' AND " +
                        "intent_pending_composition_id IS NULL) OR " +
                        "(command_kind = 'DiscardPendingComposition' AND " +
                        "intent_pending_composition_id IS NOT NULL)");
                });
            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_pending_composition_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id").ValueGeneratedNever();
            builder.Property(command => command.CommandKind)
                .HasColumnName("command_kind").HasColumnType("text").IsRequired();
            builder.Property(command => command.OrderId)
                .HasColumnName("order_id").ValueGeneratedNever();
            builder.Property(command => command.IntentPendingCompositionId)
                .HasColumnName("intent_pending_composition_id");
            builder.Property(command => command.ResultPendingCompositionId)
                .HasColumnName("result_pending_composition_id").ValueGeneratedNever();
            builder.Property(command => command.ResultCreatedAt)
                .HasColumnName("result_created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.Property(command => command.ResultCreatedByIdentityId)
                .HasColumnName("result_created_by_identity_id")
                .ValueGeneratedNever();
            builder.HasIndex(command => command.OrderId)
                .HasDatabaseName("IX_pending_composition_commands_order");
            builder.HasOne<Order>().WithMany()
                .HasForeignKey(command => command.OrderId)
                .HasConstraintName("FK_pending_composition_commands_orders")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class IncorporationConfiguration : IEntityTypeConfiguration<Incorporation>
    {
        public void Configure(EntityTypeBuilder<Incorporation> builder)
        {
            builder.ToTable(
                "incorporations",
                table => table.HasCheckConstraint(
                    "CK_order_operations_incorporations_ordinal_positive",
                    "ordinal > 0"));
            builder.HasKey(incorporation => incorporation.Id)
                .HasName("PK_order_operations_incorporations");
            builder.Property(incorporation => incorporation.Id)
                .HasColumnName("id").ValueGeneratedNever();
            builder.Property(incorporation => incorporation.OrderId)
                .HasColumnName("order_id").ValueGeneratedNever();
            builder.Property(incorporation => incorporation.Ordinal)
                .HasColumnName("ordinal").IsRequired();
            builder.HasIndex(incorporation => new
            {
                incorporation.OrderId,
                incorporation.Ordinal
            })
                .HasDatabaseName("UX_order_operations_incorporations_order_ordinal")
                .IsUnique();
            builder.HasOne<Order>().WithMany()
                .HasForeignKey(incorporation => incorporation.OrderId)
                .HasConstraintName("FK_order_operations_incorporations_orders")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class IncorporationContentConfiguration :
        IEntityTypeConfiguration<IncorporationContent>
    {
        public void Configure(EntityTypeBuilder<IncorporationContent> builder)
        {
            builder.ToTable(
                "incorporation_contents",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_order_operations_incorporation_contents_quantity_positive",
                        "quantity > 0");
                    table.HasCheckConstraint(
                        "CK_order_operations_incorporation_contents_price_non_negative",
                        "applied_price >= 0");
                    table.HasCheckConstraint(
                        "CK_order_operations_incorporation_contents_ordinal_positive",
                        "content_ordinal > 0");
                });
            builder.HasKey(content => new
            {
                content.IncorporationId,
                content.ContentOrdinal
            })
                .HasName("PK_order_operations_incorporation_contents");
            builder.Property(content => content.IncorporationId)
                .HasColumnName("incorporation_id").ValueGeneratedNever();
            builder.Property(content => content.ContentOrdinal)
                .HasColumnName("content_ordinal").ValueGeneratedNever();
            builder.Property(content => content.ProductId)
                .HasColumnName("product_id").ValueGeneratedNever();
            builder.Property(content => content.Quantity)
                .HasColumnName("quantity").IsRequired();
            builder.Property(content => content.RequiresPreparationAtConfirmation)
                .HasColumnName("requires_preparation_at_confirmation")
                .IsRequired();
            builder.Property(content => content.AppliedPrice)
                .HasColumnName("applied_price").HasColumnType("numeric").IsRequired();
            builder.Property(content => content.Instruction)
                .HasColumnName("instruction").HasColumnType("text");
            builder.HasOne<Incorporation>().WithMany()
                .HasForeignKey(content => content.IncorporationId)
                .HasConstraintName("FK_order_operations_incorporation_contents_incorporations")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class ConfirmationHistoryConfiguration :
        IEntityTypeConfiguration<ConfirmationHistory>
    {
        public void Configure(EntityTypeBuilder<ConfirmationHistory> builder)
        {
            builder.ToTable(
                "confirmation_history",
                table => table.HasCheckConstraint(
                    "CK_order_operations_confirmation_history_context_not_empty",
                    "length(btrim(confirmed_context)) > 0"));
            builder.HasKey(history => history.Id)
                .HasName("PK_order_operations_confirmation_history");
            builder.Property(history => history.Id)
                .HasColumnName("id").ValueGeneratedNever();
            builder.Property(history => history.IncorporationId)
                .HasColumnName("incorporation_id").ValueGeneratedNever();
            builder.Property(history => history.ConfirmedContext)
                .HasColumnName("confirmed_context").HasColumnType("text").IsRequired();
            builder.Property(history => history.ActorIdentityId)
                .HasColumnName("actor_identity_id");
            builder.Property(history => history.OccurredAt)
                .HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
            builder.HasIndex(history => history.IncorporationId)
                .HasDatabaseName("UX_order_operations_confirmation_history_incorporation")
                .IsUnique();
            builder.HasOne<Incorporation>().WithMany()
                .HasForeignKey(history => history.IncorporationId)
                .HasConstraintName("FK_order_operations_confirmation_history_incorporations")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class FirstConfirmationCommandConfiguration :
        IEntityTypeConfiguration<FirstConfirmationCommand>
    {
        public void Configure(EntityTypeBuilder<FirstConfirmationCommand> builder)
        {
            builder.ToTable(
                "first_confirmation_commands",
                table => table.HasCheckConstraint(
                    "CK_order_operations_first_confirmation_commands_context_not_empty",
                    "length(btrim(intent_context)) > 0"));
            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_order_operations_first_confirmation_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id");
            builder.Property(command => command.IntentContext)
                .HasColumnName("intent_context").HasColumnType("text").IsRequired();
            builder.Property(command => command.ResultIncorporationId)
                .HasColumnName("result_incorporation_id").ValueGeneratedNever();
            builder.HasIndex(command => command.ResultIncorporationId)
                .HasDatabaseName(
                    "UX_order_operations_first_confirmation_commands_result_incorporation")
                .IsUnique();
            builder.HasOne<Incorporation>().WithMany()
                .HasForeignKey(command => command.ResultIncorporationId)
                .HasConstraintName(
                    "FK_order_operations_first_confirmation_commands_incorporations")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class FirstConfirmationCommandContentConfiguration :
        IEntityTypeConfiguration<FirstConfirmationCommandContent>
    {
        public void Configure(EntityTypeBuilder<FirstConfirmationCommandContent> builder)
        {
            builder.ToTable(
                "first_confirmation_command_contents",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_order_operations_first_confirmation_command_contents_quantity_positive",
                        "quantity > 0");
                    table.HasCheckConstraint(
                        "CK_order_operations_first_command_contents_ordinal_positive",
                        "line_ordinal > 0");
                });
            builder.HasKey(content => new
            {
                content.IdempotencyKey,
                content.LineOrdinal
            })
                .HasName("PK_order_operations_first_confirmation_command_contents");
            builder.Property(content => content.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(content => content.LineOrdinal)
                .HasColumnName("line_ordinal").ValueGeneratedNever();
            builder.Property(content => content.ProductId)
                .HasColumnName("product_id").ValueGeneratedNever();
            builder.Property(content => content.Quantity)
                .HasColumnName("quantity").IsRequired();
            builder.Property(content => content.Instruction)
                .HasColumnName("instruction").HasColumnType("text");
            builder.HasOne<FirstConfirmationCommand>().WithMany()
                .HasForeignKey(content => content.IdempotencyKey)
                .HasConstraintName(
                    "FK_order_operations_first_confirmation_command_contents_commands")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class SubsequentConfirmationCommandConfiguration :
        IEntityTypeConfiguration<SubsequentConfirmationCommand>
    {
        public void Configure(EntityTypeBuilder<SubsequentConfirmationCommand> builder)
        {
            builder.ToTable("subsequent_confirmation_commands");
            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_order_operations_subsequent_confirmation_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id");
            builder.Property(command => command.IntentOrderId)
                .HasColumnName("intent_order_id").ValueGeneratedNever();
            builder.Property(command => command.IntentPendingCompositionId)
                .HasColumnName("intent_pending_composition_id");
            builder.Property(command => command.ResultIncorporationId)
                .HasColumnName("result_incorporation_id").ValueGeneratedNever();
            builder.HasIndex(command => command.IntentOrderId)
                .HasDatabaseName(
                    "IX_order_operations_subsequent_confirmation_commands_intent_order");
            builder.HasIndex(command => command.ResultIncorporationId)
                .HasDatabaseName(
                    "UX_order_operations_subsequent_confirmation_commands_result_incorporation")
                .IsUnique();
            builder.HasOne<Order>().WithMany()
                .HasForeignKey(command => command.IntentOrderId)
                .HasConstraintName(
                    "FK_order_operations_subsequent_confirmation_commands_orders")
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<Incorporation>().WithMany()
                .HasForeignKey(command => command.ResultIncorporationId)
                .HasConstraintName(
                    "FK_order_operations_subsequent_confirmation_commands_incorporations")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class SubsequentConfirmationCommandContentConfiguration :
        IEntityTypeConfiguration<SubsequentConfirmationCommandContent>
    {
        public void Configure(EntityTypeBuilder<SubsequentConfirmationCommandContent> builder)
        {
            builder.ToTable(
                "subsequent_confirmation_command_contents",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_order_operations_subsequent_confirmation_command_contents_quantity_positive",
                        "quantity > 0");
                    table.HasCheckConstraint(
                        "CK_order_operations_subseq_command_contents_ordinal_positive",
                        "line_ordinal > 0");
                });
            builder.HasKey(content => new
            {
                content.IdempotencyKey,
                content.LineOrdinal
            })
                .HasName(
                    "PK_order_operations_subsequent_confirmation_command_contents");
            builder.Property(content => content.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(content => content.LineOrdinal)
                .HasColumnName("line_ordinal").ValueGeneratedNever();
            builder.Property(content => content.ProductId)
                .HasColumnName("product_id").ValueGeneratedNever();
            builder.Property(content => content.Quantity)
                .HasColumnName("quantity").IsRequired();
            builder.Property(content => content.Instruction)
                .HasColumnName("instruction").HasColumnType("text");
            builder.HasOne<SubsequentConfirmationCommand>().WithMany()
                .HasForeignKey(content => content.IdempotencyKey)
                .HasConstraintName(
                    "FK_order_operations_subsequent_confirmation_command_contents_commands")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
