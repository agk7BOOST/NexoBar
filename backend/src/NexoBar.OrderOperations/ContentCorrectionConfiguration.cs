using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.OrderOperations;

internal sealed class ContentCorrectionHistoryConfiguration : IEntityTypeConfiguration<ContentCorrectionHistory>
{
    public void Configure(EntityTypeBuilder<ContentCorrectionHistory> builder)
    {
        builder.ToTable("content_correction_history", table =>
        {
            table.HasCheckConstraint("CK_content_correction_history_kind", "event_kind = 'ContentQuantityCorrected'");
            table.HasCheckConstraint("CK_content_correction_history_quantities",
                "corrected_quantity > 0 AND confirmed_quantity > 0 AND previous_removed_by_correction_quantity >= 0 AND " +
                "resulting_fulfillment_quantity >= 0 AND " +
                "previous_removed_by_correction_quantity::bigint + corrected_quantity::bigint = resulting_removed_by_correction_quantity::bigint AND " +
                "previous_fulfillment_quantity::bigint - corrected_quantity::bigint = resulting_fulfillment_quantity::bigint AND " +
                "confirmed_quantity::bigint - previous_removed_by_correction_quantity::bigint >= previous_fulfillment_quantity::bigint");
        });
        builder.HasKey(x => x.Id).HasName("PK_content_correction_history");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.EventKind).HasColumnName("event_kind").HasMaxLength(40).IsRequired();
        builder.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        builder.Property(x => x.IncorporationId).HasColumnName("incorporation_id").ValueGeneratedNever();
        builder.Property(x => x.ContentOrdinal).HasColumnName("content_ordinal");
        builder.Property(x => x.CorrectedQuantity).HasColumnName("corrected_quantity");
        builder.Property(x => x.ConfirmedQuantity).HasColumnName("confirmed_quantity");
        builder.Property(x => x.PreviousRemovedByCorrectionQuantity).HasColumnName("previous_removed_by_correction_quantity");
        builder.Property(x => x.ResultingRemovedByCorrectionQuantity).HasColumnName("resulting_removed_by_correction_quantity");
        builder.Property(x => x.PreviousFulfillmentQuantity).HasColumnName("previous_fulfillment_quantity");
        builder.Property(x => x.ResultingFulfillmentQuantity).HasColumnName("resulting_fulfillment_quantity");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id").ValueGeneratedNever();
        builder.HasIndex(x => new { x.OrderId, x.OccurredAt, x.Id }).HasDatabaseName("IX_content_correction_history_order");
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId)
            .HasConstraintName("FK_content_correction_history_order").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<IncorporationContent>().WithMany().HasForeignKey(x => new { x.IncorporationId, x.ContentOrdinal })
            .HasConstraintName("FK_content_correction_history_content").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.IncorporationId, x.ContentOrdinal }).HasDatabaseName("IX_content_correction_history_content");
    }
}

internal sealed class ContentCorrectionCommandConfiguration : IEntityTypeConfiguration<ContentCorrectionCommand>
{
    public void Configure(EntityTypeBuilder<ContentCorrectionCommand> builder)
    {
        builder.ToTable("content_correction_commands", table =>
        {
            table.HasCheckConstraint("CK_content_correction_commands_kind", "command_kind = 'CorrectContentQuantity'");
            table.HasCheckConstraint("CK_content_correction_commands_quantities",
                "corrected_quantity > 0 AND confirmed_quantity > 0 AND previous_removed_by_correction_quantity >= 0 AND " +
                "resulting_fulfillment_quantity >= 0 AND " +
                "previous_removed_by_correction_quantity::bigint + corrected_quantity::bigint = resulting_removed_by_correction_quantity::bigint AND " +
                "previous_fulfillment_quantity::bigint - corrected_quantity::bigint = resulting_fulfillment_quantity::bigint AND " +
                "confirmed_quantity::bigint - previous_removed_by_correction_quantity::bigint >= previous_fulfillment_quantity::bigint");
        });
        builder.HasKey(x => x.IdempotencyKey).HasName("PK_content_correction_commands");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").ValueGeneratedNever();
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id").ValueGeneratedNever();
        builder.Property(x => x.CommandKind).HasColumnName("command_kind").HasMaxLength(40).IsRequired();
        builder.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        builder.Property(x => x.IncorporationId).HasColumnName("incorporation_id").ValueGeneratedNever();
        builder.Property(x => x.ContentOrdinal).HasColumnName("content_ordinal");
        builder.Property(x => x.CorrectedQuantity).HasColumnName("corrected_quantity");
        builder.Property(x => x.ResultHistoryId).HasColumnName("result_history_id").ValueGeneratedNever();
        builder.Property(x => x.ConfirmedQuantity).HasColumnName("confirmed_quantity");
        builder.Property(x => x.PreviousRemovedByCorrectionQuantity).HasColumnName("previous_removed_by_correction_quantity");
        builder.Property(x => x.ResultingRemovedByCorrectionQuantity).HasColumnName("resulting_removed_by_correction_quantity");
        builder.Property(x => x.PreviousFulfillmentQuantity).HasColumnName("previous_fulfillment_quantity");
        builder.Property(x => x.ResultingFulfillmentQuantity).HasColumnName("resulting_fulfillment_quantity");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
        builder.HasIndex(x => x.ResultHistoryId).IsUnique().HasDatabaseName("UX_content_correction_commands_history");
        builder.HasOne<ContentCorrectionHistory>().WithOne().HasForeignKey<ContentCorrectionCommand>(x => x.ResultHistoryId)
            .HasConstraintName("FK_content_correction_commands_history").OnDelete(DeleteBehavior.Restrict);
    }
}
