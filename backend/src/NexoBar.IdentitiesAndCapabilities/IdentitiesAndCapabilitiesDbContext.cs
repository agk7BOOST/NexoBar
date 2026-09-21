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

    internal DbSet<IdentityAdministrativeCommand> AdministrativeCommands =>
        Set<IdentityAdministrativeCommand>();

    internal DbSet<InstallationProvisioningFact> InstallationProvisioningFacts =>
        Set<InstallationProvisioningFact>();

    internal DbSet<InstallationRecoveryState> InstallationRecoveryStates =>
        Set<InstallationRecoveryState>();

    internal DbSet<ExtraordinaryGeneralConfigurationRecoveryCommand>
        ExtraordinaryGeneralConfigurationRecoveryCommands =>
        Set<ExtraordinaryGeneralConfigurationRecoveryCommand>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("identities_and_capabilities");
        modelBuilder.ApplyConfiguration(new IdentityConfiguration());
        modelBuilder.ApplyConfiguration(new LocalCredentialConfiguration());
        modelBuilder.ApplyConfiguration(new IdentitySessionConfiguration());
        modelBuilder.ApplyConfiguration(new ResponsibilityAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new PreparationEnablementConfiguration());
        modelBuilder.ApplyConfiguration(new IdentityAdministrativeCommandConfiguration());
        modelBuilder.ApplyConfiguration(new InstallationProvisioningFactConfiguration());
        modelBuilder.ApplyConfiguration(new InstallationRecoveryStateConfiguration());
        modelBuilder.ApplyConfiguration(
            new ExtraordinaryGeneralConfigurationRecoveryCommandConfiguration());
    }

    private sealed class InstallationProvisioningFactConfiguration :
        IEntityTypeConfiguration<InstallationProvisioningFact>
    {
        public void Configure(EntityTypeBuilder<InstallationProvisioningFact> builder)
        {
            builder.ToTable(
                "installation_provisioning",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_installation_provisioning_singleton",
                        "singleton_key = 1");
                    table.HasCheckConstraint(
                        "CK_installation_provisioning_origin",
                        "origin IN ('InitialProvisioning', 'LegacyBackfill')");
                    table.HasCheckConstraint(
                        "CK_installation_provisioning_initial_fields",
                        "(origin = 'InitialProvisioning' AND " +
                        "completed_at IS NOT NULL AND " +
                        "provisioning_command_id IS NOT NULL AND " +
                        "initial_identity_id IS NOT NULL AND " +
                        "retry_intent_fingerprint IS NOT NULL AND " +
                        "octet_length(retry_intent_fingerprint) > 0 AND " +
                        "retry_secret_verifier IS NOT NULL AND " +
                        "length(retry_secret_verifier) > 0) OR " +
                        "(origin = 'LegacyBackfill' AND " +
                        "completed_at IS NULL AND " +
                        "provisioning_command_id IS NULL AND " +
                        "initial_identity_id IS NULL AND " +
                        "retry_intent_fingerprint IS NULL AND " +
                        "retry_secret_verifier IS NULL)");
                });
            builder.HasKey(fact => fact.Key)
                .HasName("PK_installation_provisioning");
            builder.Property(fact => fact.Key)
                .HasColumnName("singleton_key")
                .ValueGeneratedNever();
            builder.Property(fact => fact.Origin)
                .HasColumnName("origin")
                .HasConversion<string>()
                .HasColumnType("text")
                .IsRequired();
            builder.Property(fact => fact.CompletedAt)
                .HasColumnName("completed_at")
                .HasColumnType("timestamp with time zone");
            builder.Property(fact => fact.ProvisioningCommandId)
                .HasColumnName("provisioning_command_id");
            builder.Property(fact => fact.InitialIdentityId)
                .HasColumnName("initial_identity_id");
            builder.Property(fact => fact.RetryIntentFingerprint)
                .HasColumnName("retry_intent_fingerprint")
                .HasColumnType("bytea");
            builder.Property(fact => fact.RetrySecretVerifier)
                .HasColumnName("retry_secret_verifier")
                .HasColumnType("text");
            builder.Property(fact => fact.RetryRecoveryFactorVerifier)
                .HasColumnName("retry_recovery_factor_verifier")
                .HasColumnType("text");
        }
    }

    private sealed class InstallationRecoveryStateConfiguration :
        IEntityTypeConfiguration<InstallationRecoveryState>
    {
        public void Configure(EntityTypeBuilder<InstallationRecoveryState> builder)
        {
            builder.ToTable(
                "installation_recovery_state",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_installation_recovery_state_singleton",
                        "singleton_key = 1");
                    table.HasCheckConstraint(
                        "CK_installation_recovery_state_generation",
                        "generation > 0");
                    table.HasCheckConstraint(
                        "CK_installation_recovery_state_rotation_time",
                        "last_rotated_at IS NULL OR last_rotated_at >= established_at");
                    table.HasCheckConstraint(
                        "CK_installation_recovery_state_verifier",
                        "length(recovery_factor_verifier) > 0");
                });
            builder.HasKey(state => state.Key)
                .HasName("PK_installation_recovery_state");
            builder.Property(state => state.Key)
                .HasColumnName("singleton_key")
                .ValueGeneratedNever();
            builder.Property(state => state.RecoveryFactorVerifier)
                .HasColumnName("recovery_factor_verifier")
                .HasColumnType("text")
                .IsRequired();
            builder.Property(state => state.Generation)
                .HasColumnName("generation")
                .IsRequired();
            builder.Property(state => state.EstablishedAt)
                .HasColumnName("established_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.Property(state => state.LastRotatedAt)
                .HasColumnName("last_rotated_at")
                .HasColumnType("timestamp with time zone");
        }
    }

    private sealed class ExtraordinaryGeneralConfigurationRecoveryCommandConfiguration :
        IEntityTypeConfiguration<ExtraordinaryGeneralConfigurationRecoveryCommand>
    {
        public void Configure(
            EntityTypeBuilder<ExtraordinaryGeneralConfigurationRecoveryCommand> builder)
        {
            builder.ToTable(
                "extraordinary_general_configuration_recovery_commands",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_extraordinary_recovery_command_login_intent",
                        "login_intent_mode IN ('PreserveExisting', 'ExplicitIdentifier')");
                    table.HasCheckConstraint(
                        "CK_extraordinary_recovery_command_requested_login",
                        "(login_intent_mode = 'PreserveExisting' AND " +
                        "requested_login_identifier IS NULL) OR " +
                        "(login_intent_mode = 'ExplicitIdentifier' AND " +
                        "requested_login_identifier IS NOT NULL AND " +
                        "length(requested_login_identifier) > 0)");
                    table.HasCheckConstraint(
                        "CK_extraordinary_recovery_command_generation",
                        "recovery_factor_generation > 0");
                    table.HasCheckConstraint(
                        "CK_extraordinary_recovery_command_credential_verifier",
                        "length(retry_credential_verifier) > 0");
                });
            builder.HasKey(command => command.CommandId)
                .HasName("PK_extraordinary_general_configuration_recovery_commands");
            builder.Property(command => command.CommandId)
                .HasColumnName("command_id")
                .ValueGeneratedNever();
            builder.Property(command => command.TargetIdentityId)
                .HasColumnName("target_identity_id")
                .ValueGeneratedNever();
            builder.Property(command => command.LoginIntentMode)
                .HasColumnName("login_intent_mode")
                .HasConversion<string>()
                .HasColumnType("text")
                .IsRequired();
            builder.Property(command => command.RequestedLoginIdentifier)
                .HasColumnName("requested_login_identifier")
                .HasColumnType("text");
            builder.Property(command => command.RetryCredentialVerifier)
                .HasColumnName("retry_credential_verifier")
                .HasColumnType("text")
                .IsRequired();
            builder.Property(command => command.RecoveryFactorGeneration)
                .HasColumnName("recovery_factor_generation")
                .IsRequired();
            builder.Property(command => command.CompletedAt)
                .HasColumnName("completed_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            builder.HasIndex(command => command.TargetIdentityId)
                .HasDatabaseName(
                    "IX_extraordinary_recovery_commands_target_identity_id");
        }
    }

    private sealed class IdentityAdministrativeCommandConfiguration :
        IEntityTypeConfiguration<IdentityAdministrativeCommand>
    {
        public void Configure(EntityTypeBuilder<IdentityAdministrativeCommand> builder)
        {
            builder.ToTable(
                "administrative_commands",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_administrative_command_kind",
                        "command_kind IN (" +
                        "'CreateIdentity', " +
                        "'ChangeOperationalName', " +
                        "'ActivateIdentity', " +
                        "'DeactivateIdentity', " +
                        "'SetLocalCredential', " +
                        "'AssignResponsibility', " +
                        "'RevokeResponsibility', " +
                        "'GrantPreparationEnablement', " +
                        "'RevokePreparationEnablement', " +
                        "'RotateInstallationRecoveryFactor')");
                    table.HasCheckConstraint(
                        "CK_administrative_command_secret_intent",
                        "(command_kind IN ('SetLocalCredential', " +
                        "'RotateInstallationRecoveryFactor') AND " +
                        "intent_secret_verifier IS NOT NULL) OR " +
                        "(command_kind NOT IN ('SetLocalCredential', " +
                        "'RotateInstallationRecoveryFactor') AND " +
                        "intent_secret_verifier IS NULL)");
                });
            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_administrative_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key")
                .ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id")
                .ValueGeneratedNever();
            builder.Property(command => command.CommandKind)
                .HasColumnName("command_kind")
                .HasConversion<string>()
                .HasColumnType("text")
                .IsRequired();
            builder.Property(command => command.IntentFingerprint)
                .HasColumnName("intent_fingerprint")
                .HasColumnType("bytea")
                .IsRequired();
            builder.Property(command => command.IntentSecretVerifier)
                .HasColumnName("intent_secret_verifier")
                .HasColumnType("text");
            builder.Property(command => command.ResultPayload)
                .HasColumnName("result_payload")
                .HasColumnType("jsonb")
                .IsRequired();
            builder.HasOne<Identity>()
                .WithMany()
                .HasForeignKey(command => command.ActorIdentityId)
                .HasConstraintName("FK_administrative_command_actor")
                .OnDelete(DeleteBehavior.Restrict);
        }
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
