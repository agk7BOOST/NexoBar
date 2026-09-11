using Microsoft.EntityFrameworkCore;

namespace NexoBar.OrderOperations;

internal static class CompleteCancellationConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var history = model.Entity<CompleteCancellationHistory>();
        history.ToTable("complete_cancellation_history", t => t.HasCheckConstraint("CK_complete_cancellation_history_kind", "event_kind = 'OrderCompletelyCancelled'"));
        history.HasKey(x => x.Id);
        history.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        history.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        history.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id").ValueGeneratedNever();
        history.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        history.Property(x => x.PendingCompositionDiscarded).HasColumnName("pending_composition_discarded");
        history.Property(x => x.EventKind).HasColumnName("event_kind").HasMaxLength(40).IsRequired();
        history.HasAlternateKey(x => new { x.OrderId, x.Id });
        history.HasIndex(x => x.OrderId).IsUnique();
        history.HasOne<Order>().WithOne().HasForeignKey<CompleteCancellationHistory>(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);

        var state = model.Entity<OrderCancellationState>();
        state.ToTable("order_cancellation_states");
        state.HasKey(x => x.OrderId);
        state.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        state.Property(x => x.CancellationId).HasColumnName("cancellation_id").ValueGeneratedNever();
        state.HasOne<Order>().WithOne().HasForeignKey<OrderCancellationState>(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        state.HasOne<CompleteCancellationHistory>().WithOne().HasForeignKey<OrderCancellationState>(x => new { x.OrderId, x.CancellationId })
            .HasPrincipalKey<CompleteCancellationHistory>(x => new { x.OrderId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var detail = model.Entity<CompleteCancellationDetail>();
        detail.ToTable("complete_cancellation_details", t => t.HasCheckConstraint("CK_complete_cancellation_details_quantities",
            "direct_or_pending_quantity >= 0 AND in_preparation_quantity >= 0 AND ready_quantity >= 0 AND " +
            "direct_or_pending_quantity::bigint + in_preparation_quantity + ready_quantity > 0 AND resulting_fulfillment_quantity = 0"));
        detail.HasKey(x => new { x.CancellationId, x.IncorporationId, x.ContentOrdinal });
        detail.Property(x => x.CancellationId).HasColumnName("cancellation_id").ValueGeneratedNever();
        detail.Property(x => x.IncorporationId).HasColumnName("incorporation_id").ValueGeneratedNever();
        detail.Property(x => x.ContentOrdinal).HasColumnName("content_ordinal").ValueGeneratedNever();
        detail.Property(x => x.DirectOrPendingQuantity).HasColumnName("direct_or_pending_quantity");
        detail.Property(x => x.InPreparationQuantity).HasColumnName("in_preparation_quantity");
        detail.Property(x => x.ReadyQuantity).HasColumnName("ready_quantity");
        detail.Property(x => x.ResultingFulfillmentQuantity).HasColumnName("resulting_fulfillment_quantity");
        detail.HasOne<CompleteCancellationHistory>().WithMany().HasForeignKey(x => x.CancellationId).OnDelete(DeleteBehavior.Restrict);
        detail.HasOne<IncorporationContent>().WithMany().HasForeignKey(x => new { x.IncorporationId, x.ContentOrdinal }).OnDelete(DeleteBehavior.Restrict);

        var command = model.Entity<CompleteCancellationCommand>();
        command.ToTable("complete_cancellation_commands", t => t.HasCheckConstraint("CK_complete_cancellation_commands_kind", "command_kind = 'CompleteOrderCancellation'"));
        command.HasKey(x => x.IdempotencyKey);
        command.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").ValueGeneratedNever();
        command.Property(x => x.CancellationId).HasColumnName("cancellation_id").ValueGeneratedNever();
        command.Property(x => x.CommandKind).HasColumnName("command_kind").HasMaxLength(40).IsRequired();
        command.HasOne<CompleteCancellationHistory>().WithOne().HasForeignKey<CompleteCancellationCommand>(x => x.CancellationId).OnDelete(DeleteBehavior.Restrict);
    }
}
