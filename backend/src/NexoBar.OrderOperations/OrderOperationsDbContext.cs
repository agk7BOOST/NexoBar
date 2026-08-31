using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.OrderOperations;

internal sealed class OrderOperationsDbContext(
    DbContextOptions<OrderOperationsDbContext> options) : DbContext(options)
{
    internal DbSet<Order> Orders => Set<Order>();
    internal DbSet<Incorporation> Incorporations => Set<Incorporation>();
    internal DbSet<IncorporationContent> IncorporationContents => Set<IncorporationContent>();
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
        modelBuilder.ApplyConfiguration(new IncorporationConfiguration());
        modelBuilder.ApplyConfiguration(new IncorporationContentConfiguration());
        modelBuilder.ApplyConfiguration(new PreparationWorkConfiguration());
        modelBuilder.ApplyConfiguration(new PreparationHistoryConfiguration());
        modelBuilder.ApplyConfiguration(new PreparationCommandConfiguration());
        modelBuilder.ApplyConfiguration(new ConfirmationHistoryConfiguration());
        modelBuilder.ApplyConfiguration(new FirstConfirmationCommandConfiguration());
        modelBuilder.ApplyConfiguration(new FirstConfirmationCommandContentConfiguration());
        modelBuilder.ApplyConfiguration(new SubsequentConfirmationCommandConfiguration());
        modelBuilder.ApplyConfiguration(new SubsequentConfirmationCommandContentConfiguration());
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
                        "total_quantity > 0");
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
            builder.Property(command => command.IntentOrderId)
                .HasColumnName("intent_order_id").ValueGeneratedNever();
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
