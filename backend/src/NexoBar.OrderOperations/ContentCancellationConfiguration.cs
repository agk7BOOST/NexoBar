using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.OrderOperations;

internal sealed class ContentCancellationHistoryConfiguration : IEntityTypeConfiguration<ContentCancellationHistory>
{
    public void Configure(EntityTypeBuilder<ContentCancellationHistory> builder)
    {
        builder.ToTable("content_cancellation_history", table =>
        {
            table.HasCheckConstraint("CK_content_cancellation_history_kind", "event_kind = 'ContentQuantityCancelled'");
            table.HasCheckConstraint("CK_content_cancellation_history_quantities",
                "cancelled_quantity > 0 AND confirmed_quantity > 0 AND previous_cancelled_quantity >= 0 AND " +
                "resulting_fulfillment_quantity >= 0 AND " +
                "previous_cancelled_quantity::bigint + cancelled_quantity::bigint = resulting_cancelled_quantity::bigint AND " +
                "previous_fulfillment_quantity::bigint - cancelled_quantity::bigint = resulting_fulfillment_quantity::bigint AND " +
                "confirmed_quantity::bigint - previous_cancelled_quantity::bigint >= previous_fulfillment_quantity::bigint");
        });
        builder.HasKey(x => x.Id).HasName("PK_content_cancellation_history");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.EventKind).HasColumnName("event_kind").HasMaxLength(40).IsRequired();
        builder.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        builder.Property(x => x.IncorporationId).HasColumnName("incorporation_id").ValueGeneratedNever();
        builder.Property(x => x.ContentOrdinal).HasColumnName("content_ordinal");
        builder.Property(x => x.CancelledQuantity).HasColumnName("cancelled_quantity");
        builder.Property(x => x.ConfirmedQuantity).HasColumnName("confirmed_quantity");
        builder.Property(x => x.PreviousCancelledQuantity).HasColumnName("previous_cancelled_quantity");
        builder.Property(x => x.ResultingCancelledQuantity).HasColumnName("resulting_cancelled_quantity");
        builder.Property(x => x.PreviousFulfillmentQuantity).HasColumnName("previous_fulfillment_quantity");
        builder.Property(x => x.ResultingFulfillmentQuantity).HasColumnName("resulting_fulfillment_quantity");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id").ValueGeneratedNever();
        builder.HasIndex(x => new { x.OrderId, x.OccurredAt, x.Id }).HasDatabaseName("IX_content_cancellation_history_order");
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId)
            .HasConstraintName("FK_content_cancellation_history_order").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<IncorporationContent>().WithMany().HasForeignKey(x => new { x.IncorporationId, x.ContentOrdinal })
            .HasConstraintName("FK_content_cancellation_history_content").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.IncorporationId, x.ContentOrdinal }).HasDatabaseName("IX_content_cancellation_history_content");
    }
}

internal sealed class ContentCancellationCommandConfiguration : IEntityTypeConfiguration<ContentCancellationCommand>
{
    public void Configure(EntityTypeBuilder<ContentCancellationCommand> builder)
    {
        builder.ToTable("content_cancellation_commands", table =>
        {
            table.HasCheckConstraint("CK_content_cancellation_commands_kind", "command_kind = 'CancelContentQuantity'");
            table.HasCheckConstraint("CK_content_cancellation_commands_quantities",
                "cancelled_quantity > 0 AND confirmed_quantity > 0 AND previous_cancelled_quantity >= 0 AND " +
                "resulting_fulfillment_quantity >= 0 AND " +
                "previous_cancelled_quantity::bigint + cancelled_quantity::bigint = resulting_cancelled_quantity::bigint AND " +
                "previous_fulfillment_quantity::bigint - cancelled_quantity::bigint = resulting_fulfillment_quantity::bigint AND " +
                "confirmed_quantity::bigint - previous_cancelled_quantity::bigint >= previous_fulfillment_quantity::bigint");
        });
        builder.HasKey(x => x.IdempotencyKey).HasName("PK_content_cancellation_commands");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").ValueGeneratedNever();
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id").ValueGeneratedNever();
        builder.Property(x => x.CommandKind).HasColumnName("command_kind").HasMaxLength(40).IsRequired();
        builder.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        builder.Property(x => x.IncorporationId).HasColumnName("incorporation_id").ValueGeneratedNever();
        builder.Property(x => x.ContentOrdinal).HasColumnName("content_ordinal");
        builder.Property(x => x.CancelledQuantity).HasColumnName("cancelled_quantity");
        builder.Property(x => x.ResultHistoryId).HasColumnName("result_history_id").ValueGeneratedNever();
        builder.Property(x => x.ConfirmedQuantity).HasColumnName("confirmed_quantity");
        builder.Property(x => x.PreviousCancelledQuantity).HasColumnName("previous_cancelled_quantity");
        builder.Property(x => x.ResultingCancelledQuantity).HasColumnName("resulting_cancelled_quantity");
        builder.Property(x => x.PreviousFulfillmentQuantity).HasColumnName("previous_fulfillment_quantity");
        builder.Property(x => x.ResultingFulfillmentQuantity).HasColumnName("resulting_fulfillment_quantity");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
        builder.HasIndex(x => x.ResultHistoryId).IsUnique().HasDatabaseName("UX_content_cancellation_commands_history");
        builder.HasOne<ContentCancellationHistory>().WithOne().HasForeignKey<ContentCancellationCommand>(x => x.ResultHistoryId)
            .HasConstraintName("FK_content_cancellation_commands_history").OnDelete(DeleteBehavior.Restrict);
    }
}
