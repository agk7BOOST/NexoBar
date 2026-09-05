using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.OrderOperations;

internal sealed class ClosureConfiguration : IEntityTypeConfiguration<Closure>
{
    public void Configure(EntityTypeBuilder<Closure> builder)
    {
        builder.ToTable("closures");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        builder.Property(x => x.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id").ValueGeneratedNever();
        builder.HasIndex(x => x.OrderId).IsUnique();
        builder.HasOne<Order>().WithOne().HasForeignKey<Closure>(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ClosureHistoryConfiguration : IEntityTypeConfiguration<ClosureHistory>
{
    public void Configure(EntityTypeBuilder<ClosureHistory> builder)
    {
        builder.ToTable("closure_history", table =>
            table.HasCheckConstraint("CK_closure_history_event_kind", "event_kind = 'Closed'"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.ClosureId).HasColumnName("closure_id").ValueGeneratedNever();
        builder.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        builder.Property(x => x.EventKind).HasColumnName("event_kind").HasMaxLength(40).IsRequired();
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id").ValueGeneratedNever();
        builder.HasIndex(x => x.ClosureId).IsUnique();
        builder.HasIndex(x => new { x.OrderId, x.OccurredAt, x.Id });
        builder.HasOne<Closure>().WithOne().HasForeignKey<ClosureHistory>(x => x.ClosureId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ClosureCommandConfiguration : IEntityTypeConfiguration<ClosureCommand>
{
    public void Configure(EntityTypeBuilder<ClosureCommand> builder)
    {
        builder.ToTable("closure_commands", table =>
            table.HasCheckConstraint("CK_closure_commands_kind", "command_kind = 'CloseOrder'"));
        builder.HasKey(x => x.IdempotencyKey);
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").ValueGeneratedNever();
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id").ValueGeneratedNever();
        builder.Property(x => x.CommandKind).HasColumnName("command_kind").HasMaxLength(40).IsRequired();
        builder.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        builder.Property(x => x.ResultClosureId).HasColumnName("result_closure_id").ValueGeneratedNever();
        builder.Property(x => x.ResultClosedAt).HasColumnName("result_closed_at").HasColumnType("timestamp with time zone");
        builder.HasIndex(x => x.ResultClosureId).IsUnique();
        builder.HasOne<Closure>().WithOne().HasForeignKey<ClosureCommand>(x => x.ResultClosureId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}
