using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.OrderOperations;

internal sealed class ContentAppliedPriceStateConfiguration : IEntityTypeConfiguration<ContentAppliedPriceState>
{
    public void Configure(EntityTypeBuilder<ContentAppliedPriceState> builder)
    {
        builder.ToTable("content_applied_price_states", table => table.HasCheckConstraint(
            "CK_content_applied_price_states_effective_non_negative", "effective_applied_price >= 0"));
        builder.HasKey(x => new { x.IncorporationId, x.ContentOrdinal }).HasName("PK_content_applied_price_states");
        builder.Property(x => x.IncorporationId).HasColumnName("incorporation_id").ValueGeneratedNever();
        builder.Property(x => x.ContentOrdinal).HasColumnName("content_ordinal").ValueGeneratedNever();
        builder.Property(x => x.EffectiveAppliedPrice).HasColumnName("effective_applied_price").IsRequired();
        builder.HasOne<IncorporationContent>().WithOne().HasForeignKey<ContentAppliedPriceState>(x => new { x.IncorporationId, x.ContentOrdinal })
            .HasConstraintName("FK_content_applied_price_states_content").OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AppliedPriceCorrectionHistoryConfiguration : IEntityTypeConfiguration<AppliedPriceCorrectionHistory>
{
    public void Configure(EntityTypeBuilder<AppliedPriceCorrectionHistory> builder)
    {
        builder.ToTable("applied_price_correction_history", table =>
        {
            table.HasCheckConstraint("CK_applied_price_correction_history_kind", "event_kind = 'AppliedPriceCorrected'");
            table.HasCheckConstraint("CK_applied_price_correction_history_prices", "previous_effective_applied_price >= 0 AND resulting_effective_applied_price >= 0 AND previous_effective_applied_price <> resulting_effective_applied_price");
        });
        builder.HasKey(x => x.Id).HasName("PK_applied_price_correction_history");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.EventKind).HasColumnName("event_kind").HasMaxLength(40).IsRequired();
        builder.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        builder.Property(x => x.IncorporationId).HasColumnName("incorporation_id").ValueGeneratedNever();
        builder.Property(x => x.ContentOrdinal).HasColumnName("content_ordinal");
        builder.Property(x => x.PreviousEffectiveAppliedPrice).HasColumnName("previous_effective_applied_price");
        builder.Property(x => x.ResultingEffectiveAppliedPrice).HasColumnName("resulting_effective_applied_price");
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id").ValueGeneratedNever();
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
        builder.HasIndex(x => new { x.IncorporationId, x.ContentOrdinal, x.OccurredAt, x.Id }).HasDatabaseName("IX_applied_price_correction_history_content_time");
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).HasConstraintName("FK_applied_price_correction_history_order").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<IncorporationContent>().WithMany().HasForeignKey(x => new { x.IncorporationId, x.ContentOrdinal }).HasConstraintName("FK_applied_price_correction_history_content").OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AppliedPriceCorrectionCommandConfiguration : IEntityTypeConfiguration<AppliedPriceCorrectionCommand>
{
    public void Configure(EntityTypeBuilder<AppliedPriceCorrectionCommand> builder)
    {
        builder.ToTable("applied_price_correction_commands", table => table.HasCheckConstraint(
            "CK_applied_price_correction_commands_kind", "command_kind = 'ApplyCurrentCatalogPrice'"));
        builder.HasKey(x => x.IdempotencyKey).HasName("PK_applied_price_correction_commands");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").ValueGeneratedNever();
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id").ValueGeneratedNever();
        builder.Property(x => x.CommandKind).HasColumnName("command_kind").HasMaxLength(40).IsRequired();
        builder.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        builder.Property(x => x.IncorporationId).HasColumnName("incorporation_id").ValueGeneratedNever();
        builder.Property(x => x.ContentOrdinal).HasColumnName("content_ordinal");
        builder.Property(x => x.ResultHistoryId).HasColumnName("result_history_id").ValueGeneratedNever();
        builder.Property(x => x.ResultPreviousEffectiveAppliedPrice).HasColumnName("result_previous_effective_applied_price");
        builder.Property(x => x.ResultEffectiveAppliedPrice).HasColumnName("result_effective_applied_price");
        builder.Property(x => x.ResultOccurredAt).HasColumnName("result_occurred_at").HasColumnType("timestamp with time zone");
        builder.HasIndex(x => x.ResultHistoryId).IsUnique().HasDatabaseName("UX_applied_price_correction_commands_history");
        builder.HasOne<AppliedPriceCorrectionHistory>().WithOne().HasForeignKey<AppliedPriceCorrectionCommand>(x => x.ResultHistoryId)
            .HasConstraintName("FK_applied_price_correction_commands_history").OnDelete(DeleteBehavior.Restrict);
    }
}
