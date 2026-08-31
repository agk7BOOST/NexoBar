using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class IdentitiesAndCapabilitiesDbContext(
    DbContextOptions<IdentitiesAndCapabilitiesDbContext> options) : DbContext(options)
{
    internal DbSet<Identity> Identities => Set<Identity>();

    internal DbSet<LocalCredential> LocalCredentials => Set<LocalCredential>();

    internal DbSet<IdentitySession> Sessions => Set<IdentitySession>();

    internal DbSet<ResponsibilityAssignment> ResponsibilityAssignments =>
        Set<ResponsibilityAssignment>();

    internal DbSet<PreparationEnablement> PreparationEnablements =>
        Set<PreparationEnablement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("identities_and_capabilities");
        modelBuilder.ApplyConfiguration(new IdentityConfiguration());
        modelBuilder.ApplyConfiguration(new LocalCredentialConfiguration());
        modelBuilder.ApplyConfiguration(new IdentitySessionConfiguration());
        modelBuilder.ApplyConfiguration(new ResponsibilityAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new PreparationEnablementConfiguration());
    }

    private sealed class LocalCredentialConfiguration :
        IEntityTypeConfiguration<LocalCredential>
    {
        public void Configure(EntityTypeBuilder<LocalCredential> builder)
        {
            builder.ToTable(
                "local_credentials",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_local_credential_login_identifier",
                        "login_identifier = btrim(login_identifier) AND " +
                        "length(login_identifier) > 0");
                    table.HasCheckConstraint(
                        "CK_local_credential_normalized_login",
                        "normalized_login_identifier = " +
                        "btrim(normalized_login_identifier) AND " +
                        "length(normalized_login_identifier) > 0");
                    table.HasCheckConstraint(
                        "CK_local_credential_secret_verifier",
                        "length(secret_verifier) > 0");
                });
            builder.HasKey(credential => credential.IdentityId)
                .HasName("PK_local_credentials");
            builder.Property(credential => credential.IdentityId)
                .HasColumnName("identity_id")
                .ValueGeneratedNever();
            builder.Property(credential => credential.LoginIdentifier)
                .HasColumnName("login_identifier")
                .HasColumnType("text")
                .IsRequired();
            builder.Property(credential => credential.NormalizedLoginIdentifier)
                .HasColumnName("normalized_login_identifier")
                .HasColumnType("text")
                .IsRequired();
            builder.Property(credential => credential.SecretVerifier)
                .HasColumnName("secret_verifier")
                .HasColumnType("text")
                .IsRequired();
            builder.HasIndex(credential => credential.NormalizedLoginIdentifier)
                .IsUnique()
                .HasDatabaseName("UX_local_credential_normalized_login");
            builder.HasOne<Identity>()
                .WithOne()
                .HasForeignKey<LocalCredential>(credential => credential.IdentityId)
                .HasConstraintName("FK_local_credential_identity")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class IdentitySessionConfiguration :
        IEntityTypeConfiguration<IdentitySession>
    {
        public void Configure(EntityTypeBuilder<IdentitySession> builder)
        {
            builder.ToTable(
                "sessions",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_session_absolute_expiration",
                        "absolute_expires_at > created_at");
                    table.HasCheckConstraint(
                        "CK_session_last_activity",
                        "last_activity_at >= created_at AND " +
                        "last_activity_at < absolute_expires_at");
                    table.HasCheckConstraint(
                        "CK_session_revoked_at",
                        "revoked_at IS NULL OR revoked_at >= created_at");
                });
            builder.HasKey(session => session.Id)
                .HasName("PK_sessions");
            builder.Property(session => session.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            builder.Property(session => session.IdentityId)
                .HasColumnName("identity_id")
                .ValueGeneratedNever();
            builder.Property(session => session.TokenHash)
                .HasColumnName("token_hash")
                .HasColumnType("bytea")
                .IsRequired();
            builder.Property(session => session.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.Property(session => session.LastActivityAt)
                .HasColumnName("last_activity_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.Property(session => session.AbsoluteExpiresAt)
                .HasColumnName("absolute_expires_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.Property(session => session.RevokedAt)
                .HasColumnName("revoked_at")
                .HasColumnType("timestamp with time zone");
            builder.HasIndex(session => session.TokenHash)
                .IsUnique()
                .HasDatabaseName("UX_session_token_hash");
            builder.HasIndex(session => session.IdentityId)
                .HasDatabaseName("IX_session_identity_id");
            builder.HasOne<Identity>()
                .WithMany()
                .HasForeignKey(session => session.IdentityId)
                .HasConstraintName("FK_session_identity")
                .OnDelete(DeleteBehavior.Restrict);
        }
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
