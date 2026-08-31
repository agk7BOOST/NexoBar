using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class IdentitiesAndCapabilitiesDbContext(
    DbContextOptions<IdentitiesAndCapabilitiesDbContext> options) : DbContext(options)
{
    internal DbSet<Identity> Identities => Set<Identity>();

    internal DbSet<ResponsibilityAssignment> ResponsibilityAssignments =>
        Set<ResponsibilityAssignment>();

    internal DbSet<PreparationEnablement> PreparationEnablements =>
        Set<PreparationEnablement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("identities_and_capabilities");
        modelBuilder.ApplyConfiguration(new IdentityConfiguration());
        modelBuilder.ApplyConfiguration(new ResponsibilityAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new PreparationEnablementConfiguration());
    }

    private sealed class IdentityConfiguration : IEntityTypeConfiguration<Identity>
    {
        public void Configure(EntityTypeBuilder<Identity> builder)
        {
            builder.ToTable(
                "identities",
                table => table.HasCheckConstraint(
                    "CK_identity_operational_name_trimmed_not_empty",
                    "operational_name = btrim(operational_name) AND " +
                    "length(operational_name) > 0"));
            builder.HasKey(identity => identity.Id)
                .HasName("PK_identities_and_capabilities_identities");
            builder.Property(identity => identity.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            builder.Property(identity => identity.OperationalName)
                .HasColumnName("operational_name")
                .HasColumnType("text")
                .IsRequired();
            builder.Property(identity => identity.NormalizedOperationalName)
                .HasColumnName("normalized_operational_name")
                .HasColumnType("text")
                .HasComputedColumnSql("lower(operational_name)", stored: true);
            builder.Property(identity => identity.IsActive)
                .HasColumnName("is_active")
                .IsRequired();
            builder.HasIndex(identity => identity.NormalizedOperationalName)
                .HasDatabaseName("IX_identity_normalized_operational_name");
        }
    }

    private sealed class ResponsibilityAssignmentConfiguration :
        IEntityTypeConfiguration<ResponsibilityAssignment>
    {
        public void Configure(EntityTypeBuilder<ResponsibilityAssignment> builder)
        {
            builder.ToTable(
                "responsibility_assignments",
                table => table.HasCheckConstraint(
                    "CK_responsibility_assignment_code",
                    "responsibility_code IN (" +
                    "'OrderOperationsAndBasicClosure', " +
                    "'OperationalIntervention', " +
                    "'Preparation', " +
                    "'CatalogConfiguration', " +
                    "'InventoryOperation', " +
                    "'InventoryConfiguration', " +
                    "'GeneralConfiguration')"));
            builder.HasKey(assignment => new
            {
                assignment.IdentityId,
                assignment.ResponsibilityCode
            })
                .HasName("PK_responsibility_assignments");
            builder.Property(assignment => assignment.IdentityId)
                .HasColumnName("identity_id")
                .ValueGeneratedNever();
            builder.Property(assignment => assignment.ResponsibilityCode)
                .HasColumnName("responsibility_code")
                .HasConversion<string>()
                .HasColumnType("text")
                .IsRequired();
            builder.HasOne<Identity>()
                .WithMany(identity => identity.ResponsibilityAssignments)
                .HasForeignKey(assignment => assignment.IdentityId)
                .HasConstraintName("FK_responsibility_assignments_identity")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class PreparationEnablementConfiguration :
        IEntityTypeConfiguration<PreparationEnablement>
    {
        public void Configure(EntityTypeBuilder<PreparationEnablement> builder)
        {
            builder.ToTable("preparation_enablements");
            builder.HasKey(enablement => new
            {
                enablement.IdentityId,
                enablement.PreparationResponsibilityId
            })
                .HasName("PK_preparation_enablements");
            builder.Property(enablement => enablement.IdentityId)
                .HasColumnName("identity_id")
                .ValueGeneratedNever();
            builder.Property(enablement => enablement.PreparationResponsibilityId)
                .HasColumnName("preparation_responsibility_id")
                .ValueGeneratedNever();
            builder.HasOne<Identity>()
                .WithMany(identity => identity.PreparationEnablements)
                .HasForeignKey(enablement => enablement.IdentityId)
                .HasConstraintName("FK_preparation_enablements_identity")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
