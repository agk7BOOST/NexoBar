using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.OrderOperations;

internal sealed class DeliveryCorrectionHistoryConfiguration : IEntityTypeConfiguration<DeliveryCorrectionHistory>
{
    public void Configure(EntityTypeBuilder<DeliveryCorrectionHistory> builder)
    {
        builder.ToTable("delivery_correction_history", table =>
        {
            table.HasCheckConstraint("CK_delivery_correction_history_kind", "event_kind = 'DeliveryQuantityCorrected'");
            table.HasCheckConstraint("CK_delivery_correction_history_quantities",
                "corrected_quantity > 0 AND resulting_delivered_quantity >= 0 AND " +
                "previous_delivered_quantity::bigint - corrected_quantity::bigint = resulting_delivered_quantity::bigint");
        });
        builder.HasKey(x => x.Id).HasName("PK_delivery_correction_history");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.EventKind).HasColumnName("event_kind").HasMaxLength(40).IsRequired();
        builder.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        builder.Property(x => x.IncorporationId).HasColumnName("incorporation_id").ValueGeneratedNever();
        builder.Property(x => x.ContentOrdinal).HasColumnName("content_ordinal");
        builder.Property(x => x.CorrectedQuantity).HasColumnName("corrected_quantity");
        builder.Property(x => x.PreviousDeliveredQuantity).HasColumnName("previous_delivered_quantity");
        builder.Property(x => x.ResultingDeliveredQuantity).HasColumnName("resulting_delivered_quantity");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id").ValueGeneratedNever();
        builder.HasIndex(x => new { x.OrderId, x.OccurredAt, x.Id }).HasDatabaseName("IX_delivery_correction_history_order");
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId)
            .HasConstraintName("FK_delivery_correction_history_order").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<IncorporationContent>().WithMany().HasForeignKey(x => new { x.IncorporationId, x.ContentOrdinal })
            .HasConstraintName("FK_delivery_correction_history_content").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.IncorporationId, x.ContentOrdinal }).HasDatabaseName("IX_delivery_correction_history_content");
    }
}

internal sealed class DeliveryCorrectionCommandConfiguration : IEntityTypeConfiguration<DeliveryCorrectionCommand>
{
    public void Configure(EntityTypeBuilder<DeliveryCorrectionCommand> builder)
    {
        builder.ToTable("delivery_correction_commands", table =>
        {
            table.HasCheckConstraint("CK_delivery_correction_commands_kind", "command_kind = 'CorrectDeliveryQuantity'");
            table.HasCheckConstraint("CK_delivery_correction_commands_quantities",
                "corrected_quantity > 0 AND resulting_delivered_quantity >= 0 AND " +
                "previous_delivered_quantity::bigint - corrected_quantity::bigint = resulting_delivered_quantity::bigint");
        });
        builder.HasKey(x => x.IdempotencyKey).HasName("PK_delivery_correction_commands");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").ValueGeneratedNever();
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id").ValueGeneratedNever();
        builder.Property(x => x.CommandKind).HasColumnName("command_kind").HasMaxLength(40).IsRequired();
        builder.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        builder.Property(x => x.IncorporationId).HasColumnName("incorporation_id").ValueGeneratedNever();
        builder.Property(x => x.ContentOrdinal).HasColumnName("content_ordinal");
        builder.Property(x => x.CorrectedQuantity).HasColumnName("corrected_quantity");
        builder.Property(x => x.ResultHistoryId).HasColumnName("result_history_id").ValueGeneratedNever();
        builder.Property(x => x.PreviousDeliveredQuantity).HasColumnName("previous_delivered_quantity");
        builder.Property(x => x.ResultingDeliveredQuantity).HasColumnName("resulting_delivered_quantity");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
        builder.HasIndex(x => x.ResultHistoryId).IsUnique().HasDatabaseName("UX_delivery_correction_commands_history");
        builder.HasOne<DeliveryCorrectionHistory>().WithOne().HasForeignKey<DeliveryCorrectionCommand>(x => x.ResultHistoryId)
            .HasConstraintName("FK_delivery_correction_commands_history").OnDelete(DeleteBehavior.Restrict);
    }
}
