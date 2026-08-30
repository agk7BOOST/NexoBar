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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("operational_configuration");
        modelBuilder.ApplyConfiguration(new PreparationResponsibilityConfiguration());
        modelBuilder.ApplyConfiguration(
            new PreparationResponsibilityCreationCommandConfiguration());
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
            builder.HasOne<PreparationResponsibility>().WithMany()
                .HasForeignKey(command => command.ResultResponsibilityId)
                .HasConstraintName(
                    "FK_op_config_prep_responsibility_creation_cmd_responsibility")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
