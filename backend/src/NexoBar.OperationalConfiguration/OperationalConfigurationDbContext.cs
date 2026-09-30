using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.OperationalConfiguration;

internal sealed class OperationalConfigurationDbContext(
    DbContextOptions<OperationalConfigurationDbContext> options) : DbContext(options)
{
    internal DbSet<PreparationResponsibility> PreparationResponsibilities =>
        Set<PreparationResponsibility>();

    internal DbSet<PreparationResponsibilityCreationCommand>
        PreparationResponsibilityCreationCommands =>
            Set<PreparationResponsibilityCreationCommand>();
    internal DbSet<OperationalContext> Contexts => Set<OperationalContext>();
    internal DbSet<OperationalContextCreationCommand> OperationalContextCreationCommands =>
        Set<OperationalContextCreationCommand>();

    internal DbSet<OperationalContextLifecycleCommand> OperationalContextLifecycleCommands => Set<OperationalContextLifecycleCommand>();
    internal DbSet<PreparationResponsibilityLifecycleCommand> PreparationResponsibilityLifecycleCommands => Set<PreparationResponsibilityLifecycleCommand>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureContextLifecycle(modelBuilder.Entity<OperationalContextLifecycleCommand>());
        ConfigureDestinationLifecycle(modelBuilder.Entity<PreparationResponsibilityLifecycleCommand>());
        modelBuilder.HasDefaultSchema("operational_configuration");
        modelBuilder.ApplyConfiguration(new PreparationResponsibilityConfiguration());
        modelBuilder.ApplyConfiguration(
            new PreparationResponsibilityCreationCommandConfiguration());
        modelBuilder.ApplyConfiguration(new OperationalContextConfiguration());
        modelBuilder.ApplyConfiguration(new OperationalContextCreationCommandConfiguration());
    }

    private sealed class OperationalContextConfiguration :
        IEntityTypeConfiguration<OperationalContext>
    {
        public void Configure(EntityTypeBuilder<OperationalContext> builder)
        {
            builder.ToTable(
                "contexts",
                table => table.HasCheckConstraint(
                    "CK_operational_configuration_contexts_name_not_empty",
                    "length(btrim(operational_name)) > 0"));
            builder.HasKey(context => context.Id)
                .HasName("PK_operational_configuration_contexts");
            builder.Property(context => context.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(context => context.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            builder.Property(context => context.OperationalName)
                .HasColumnName("operational_name").HasColumnType("text").IsRequired();
            builder.Property(context => context.NormalizedOperationalName)
                .HasColumnName("normalized_operational_name")
                .HasColumnType("text")
                .HasComputedColumnSql("lower(operational_name)", stored: true);
            builder.HasIndex(context => context.NormalizedOperationalName)
                .HasDatabaseName("UX_operational_configuration_contexts_normalized_name")
                .IsUnique();
        }
    }

    private sealed class OperationalContextCreationCommandConfiguration :
        IEntityTypeConfiguration<OperationalContextCreationCommand>
    {
        public void Configure(EntityTypeBuilder<OperationalContextCreationCommand> builder)
        {
            builder.ToTable(
                "context_creation_commands",
                table => table.HasCheckConstraint(
                    "CK_operational_configuration_context_creation_command_names_not_empty",
                    "length(btrim(intent_operational_name)) > 0 AND " +
                    "length(btrim(result_operational_name)) > 0"));
            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_operational_configuration_context_creation_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id");
            builder.Property(command => command.IntentOperationalName)
                .HasColumnName("intent_operational_name").HasColumnType("text").IsRequired();
            builder.Property(command => command.ResultContextId)
                .HasColumnName("result_context_id").ValueGeneratedNever();
            builder.Property(command => command.ResultOperationalName)
                .HasColumnName("result_operational_name").HasColumnType("text").IsRequired();
            builder.HasIndex(command => command.ResultContextId)
                .HasDatabaseName("UX_op_config_context_creation_command_result")
                .IsUnique();

        }
    }

    private sealed class PreparationResponsibilityConfiguration :
        IEntityTypeConfiguration<PreparationResponsibility>
    {
        public void Configure(EntityTypeBuilder<PreparationResponsibility> builder)
        {
            builder.ToTable(
                "preparation_responsibilities",
                table => table.HasCheckConstraint(
                    "CK_op_config_preparation_responsibilities_name_not_empty",
                    "length(btrim(operational_name)) > 0"));
            builder.HasKey(responsibility => responsibility.Id)
                .HasName("PK_operational_configuration_preparation_responsibilities");
            builder.Property(responsibility => responsibility.Id)
                .HasColumnName("id").ValueGeneratedNever();
            builder.Property(responsibility => responsibility.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            builder.Property(responsibility => responsibility.OperationalName)
                .HasColumnName("operational_name").HasColumnType("text").IsRequired();
            builder.Property(responsibility => responsibility.NormalizedOperationalName)
                .HasColumnName("normalized_operational_name")
                .HasColumnType("text")
                .HasComputedColumnSql("lower(operational_name)", stored: true);
            builder.HasIndex(responsibility => responsibility.NormalizedOperationalName)
                .HasDatabaseName(
                    "UX_operational_configuration_responsibilities_normalized_name")
                .IsUnique();
        }
    }

    private sealed class PreparationResponsibilityCreationCommandConfiguration :
        IEntityTypeConfiguration<PreparationResponsibilityCreationCommand>
    {
        public void Configure(
            EntityTypeBuilder<PreparationResponsibilityCreationCommand> builder)
        {
            builder.ToTable(
                "preparation_responsibility_creation_commands",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_operational_configuration_responsibility_cmd_intent_name",
                        "length(btrim(intent_operational_name)) > 0");
                    table.HasCheckConstraint(
                        "CK_operational_configuration_responsibility_cmd_result_name",
                        "length(btrim(result_operational_name)) > 0");
                });
            builder.HasKey(command => command.IdempotencyKey)
                .HasName(
                    "PK_op_config_preparation_responsibility_creation_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id");
            builder.Property(command => command.CommandKind)
                .HasColumnName("command_kind").HasConversion<string>()
                .HasColumnType("text").IsRequired();
            builder.Property(command => command.IntentOperationalName)
                .HasColumnName("intent_operational_name")
                .HasColumnType("text").IsRequired();
            builder.Property(command => command.ResultResponsibilityId)
                .HasColumnName("result_responsibility_id").ValueGeneratedNever();
            builder.Property(command => command.ResultOperationalName)
                .HasColumnName("result_operational_name")
                .HasColumnType("text").IsRequired();
            builder.HasIndex(command => command.ResultResponsibilityId)
                .HasDatabaseName(
                    "UX_op_config_preparation_responsibility_creation_cmd_result")
                .IsUnique();

        }
    }
    private static void ConfigureContextLifecycle(EntityTypeBuilder<OperationalContextLifecycleCommand> builder)
    {
        builder.ToTable("context_lifecycle_commands", table => table.HasCheckConstraint("CK_context_lifecycle_commands_kind", "command_kind IN ('Rename', 'Retire', 'Reactivate', 'Delete')"));
        builder.HasKey(x => x.IdempotencyKey);
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").ValueGeneratedNever();
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id");
        builder.Property(x => x.CommandKind).HasColumnName("command_kind").HasColumnType("text").IsRequired();
        builder.Property(x => x.TargetId).HasColumnName("target_id");
        builder.Property(x => x.ExpectedOperationalName).HasColumnName("expected_operational_name").HasColumnType("text").IsRequired();
        builder.Property(x => x.ExpectedIsActive).HasColumnName("expected_is_active");
        builder.Property(x => x.NewOperationalName).HasColumnName("new_operational_name").HasColumnType("text");
        builder.Property(x => x.ResultOperationalName).HasColumnName("result_operational_name").HasColumnType("text").IsRequired();
        builder.Property(x => x.ResultIsActive).HasColumnName("result_is_active");
        builder.Property(x => x.ResultIsDeleted).HasColumnName("result_is_deleted");
    }
    private static void ConfigureDestinationLifecycle(EntityTypeBuilder<PreparationResponsibilityLifecycleCommand> builder)
    {
        builder.ToTable("preparation_responsibility_lifecycle_commands", table => table.HasCheckConstraint("CK_preparation_responsibility_lifecycle_commands_kind", "command_kind IN ('Rename', 'Retire', 'Reactivate', 'Delete')"));
        builder.HasKey(x => x.IdempotencyKey);
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").ValueGeneratedNever();
        builder.Property(x => x.ActorIdentityId).HasColumnName("actor_identity_id");
        builder.Property(x => x.CommandKind).HasColumnName("command_kind").HasColumnType("text").IsRequired();
        builder.Property(x => x.TargetId).HasColumnName("target_id");
        builder.Property(x => x.ExpectedOperationalName).HasColumnName("expected_operational_name").HasColumnType("text").IsRequired();
        builder.Property(x => x.ExpectedIsActive).HasColumnName("expected_is_active");
        builder.Property(x => x.NewOperationalName).HasColumnName("new_operational_name").HasColumnType("text");
        builder.Property(x => x.ResultOperationalName).HasColumnName("result_operational_name").HasColumnType("text").IsRequired();
        builder.Property(x => x.ResultIsActive).HasColumnName("result_is_active");
        builder.Property(x => x.ResultIsDeleted).HasColumnName("result_is_deleted");
    }
}
