using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.Catalog;

internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    internal DbSet<Product> Products => Set<Product>();

    internal DbSet<CatalogGroup> Groups => Set<CatalogGroup>();

    internal DbSet<ProductCreationCommand> ProductCreationCommands =>
        Set<ProductCreationCommand>();

    internal DbSet<ProductPriceChangeCommand> ProductPriceChangeCommands =>
        Set<ProductPriceChangeCommand>();

    internal DbSet<ProductPreparationConfigurationChangeCommand>
        ProductPreparationConfigurationChangeCommands =>
        Set<ProductPreparationConfigurationChangeCommand>();

    internal DbSet<GroupCreationCommand> GroupCreationCommands => Set<GroupCreationCommand>();
    internal DbSet<ProductGroupChangeCommand> ProductGroupChangeCommands => Set<ProductGroupChangeCommand>();
    internal DbSet<ProductOperationalNameChangeCommand> ProductOperationalNameChangeCommands => Set<ProductOperationalNameChangeCommand>();
    internal DbSet<ProductRetireCommand> ProductRetireCommands => Set<ProductRetireCommand>();
    internal DbSet<ProductReactivateCommand> ProductReactivateCommands => Set<ProductReactivateCommand>();
    internal DbSet<ProductAvailabilityChangeCommand> ProductAvailabilityChangeCommands => Set<ProductAvailabilityChangeCommand>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");
        modelBuilder.ApplyConfiguration(new ProductConfiguration());
        modelBuilder.ApplyConfiguration(new CatalogGroupConfiguration());
        modelBuilder.ApplyConfiguration(new ProductCreationCommandConfiguration());
        modelBuilder.ApplyConfiguration(new ProductPriceChangeCommandConfiguration());
        modelBuilder.ApplyConfiguration(
            new ProductPreparationConfigurationChangeCommandConfiguration());
        modelBuilder.ApplyConfiguration(new GroupCreationCommandConfiguration());
        modelBuilder.ApplyConfiguration(new ProductGroupChangeCommandConfiguration());
        modelBuilder.ApplyConfiguration(new ProductOperationalNameChangeCommandConfiguration());
        modelBuilder.ApplyConfiguration(new ProductRetireCommandConfiguration());
        modelBuilder.ApplyConfiguration(new ProductReactivateCommandConfiguration());
        modelBuilder.ApplyConfiguration(new ProductAvailabilityChangeCommandConfiguration());
    }

    private sealed class CatalogGroupConfiguration : IEntityTypeConfiguration<CatalogGroup>
    {
        public void Configure(EntityTypeBuilder<CatalogGroup> builder)
        {
            builder.ToTable("groups", table => table.HasCheckConstraint(
                "CK_catalog_groups_operational_name_not_blank",
                "length(btrim(operational_name)) > 0"));
            builder.HasKey(group => group.Id).HasName("PK_catalog_groups");
            builder.Property(group => group.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(group => group.OperationalName).HasColumnName("operational_name")
                .HasColumnType("text").IsRequired();
            builder.Property(group => group.NormalizedOperationalName)
                .HasColumnName("normalized_operational_name").HasColumnType("text")
                .HasComputedColumnSql("lower(operational_name)", stored: true);
            builder.HasIndex(group => group.NormalizedOperationalName)
                .HasDatabaseName("UX_catalog_groups_normalized_operational_name").IsUnique();
        }
    }

    private sealed class ProductPriceChangeCommandConfiguration :
        IEntityTypeConfiguration<ProductPriceChangeCommand>
    {
        public void Configure(EntityTypeBuilder<ProductPriceChangeCommand> builder)
        {
            builder.ToTable(
                "product_price_change_commands",
                tableBuilder =>
                {
                    tableBuilder.HasCheckConstraint(
                        "CK_catalog_product_price_change_commands_intent_new_price_non_negative",
                        "intent_new_price >= 0");
                    tableBuilder.HasCheckConstraint(
                        "CK_catalog_product_price_change_commands_result_price_non_negative",
                        "result_price >= 0");
                    tableBuilder.HasCheckConstraint(
                        "CK_catalog_product_price_change_commands_result_matches_intent",
                        "result_price = intent_new_price");
                });

            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_catalog_product_price_change_commands");

            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key")
                .ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id");
            builder.Property(command => command.CommandKind)
                .HasColumnName("command_kind").HasConversion<string>()
                .HasColumnType("text").IsRequired();

            builder.Property(command => command.ProductId)
                .HasColumnName("product_id")
                .ValueGeneratedNever();

            builder.Property(command => command.IntentExpectedCurrentPrice)
                .HasColumnName("intent_expected_current_price")
                .HasColumnType("numeric")
                .IsRequired();

            builder.Property(command => command.IntentNewPrice)
                .HasColumnName("intent_new_price")
                .HasColumnType("numeric")
                .IsRequired();

            builder.Property(command => command.ResultPrice)
                .HasColumnName("result_price")
                .HasColumnType("numeric")
                .IsRequired();

            builder.HasOne<Product>()
                .WithMany()
                .HasForeignKey(command => command.ProductId)
                .HasConstraintName("FK_catalog_product_price_change_commands_products")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
    {
        public void Configure(EntityTypeBuilder<Product> builder)
        {
            builder.ToTable(
                "products",
                tableBuilder =>
                {
                    tableBuilder.HasCheckConstraint(
                        "CK_catalog_products_preparation_configuration_coherent",
                        "(requires_preparation = false AND " +
                        "preparation_responsibility_id IS NULL) OR " +
                        "(requires_preparation = true AND " +
                        "preparation_responsibility_id IS NOT NULL)");
                    tableBuilder.HasCheckConstraint(
                        "CK_catalog_products_price_non_negative",
                        "price >= 0");
                });

            builder.HasKey(product => product.Id)
                .HasName("PK_catalog_products");

            builder.Property(product => product.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            builder.Property(product => product.OperationalName)
                .HasColumnName("operational_name")
                .HasColumnType("text")
                .IsRequired();

            builder.Property(product => product.NormalizedOperationalName)
                .HasColumnName("normalized_operational_name")
                .HasColumnType("text")
                .HasComputedColumnSql("lower(operational_name)", stored: true);

            builder.Property(product => product.Price)
                .HasColumnName("price")
                .HasColumnType("numeric")
                .IsRequired();

            builder.Property(product => product.IsActive)
                .HasColumnName("is_active")
                .IsRequired();

            builder.Property(product => product.IsAvailable)
                .HasColumnName("is_available")
                .IsRequired();

            builder.Property(product => product.RequiresPreparation)
                .HasColumnName("requires_preparation")
                .IsRequired();

            builder.Property(product => product.PreparationResponsibilityId)
                .HasColumnName("preparation_responsibility_id")
                .ValueGeneratedNever();

            builder.Property(product => product.GroupId).HasColumnName("group_id")
                .ValueGeneratedNever();
            builder.HasOne<CatalogGroup>().WithMany().HasForeignKey(product => product.GroupId)
                .HasConstraintName("FK_catalog_products_groups").OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(product => product.NormalizedOperationalName)
                .HasDatabaseName("UX_catalog_products_active_normalized_operational_name")
                .IsUnique()
                .HasFilter("is_active");
        }
    }

    private sealed class GroupCreationCommandConfiguration : IEntityTypeConfiguration<GroupCreationCommand>
    {
        public void Configure(EntityTypeBuilder<GroupCreationCommand> builder)
        {
            builder.ToTable("group_creation_commands", table => table.HasCheckConstraint(
                "CK_catalog_group_creation_commands_intent_name_not_blank",
                "length(btrim(intent_operational_name)) > 0"));
            builder.HasKey(command => command.IdempotencyKey).HasName("PK_catalog_group_creation_commands");
            builder.Property(command => command.IdempotencyKey).HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId).HasColumnName("actor_identity_id");
            builder.Property(command => command.CommandKind).HasColumnName("command_kind").HasConversion<string>().HasColumnType("text").IsRequired();
            builder.Property(command => command.IntentOperationalName).HasColumnName("intent_operational_name").HasColumnType("text").IsRequired();
            builder.Property(command => command.ResultGroupId).HasColumnName("result_group_id").ValueGeneratedNever();
            builder.HasIndex(command => command.ResultGroupId).HasDatabaseName("UX_catalog_group_creation_commands_result_group_id").IsUnique();
            builder.HasOne<CatalogGroup>().WithMany().HasForeignKey(command => command.ResultGroupId)
                .HasConstraintName("FK_catalog_group_creation_commands_groups").OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class ProductGroupChangeCommandConfiguration : IEntityTypeConfiguration<ProductGroupChangeCommand>
    {
        public void Configure(EntityTypeBuilder<ProductGroupChangeCommand> builder)
        {
            ConfigureProductCommand(builder, "product_group_change_commands", "PK_catalog_product_group_change_commands", "FK_catalog_product_group_change_commands_products");
            builder.Property(command => command.IntentExpectedGroupId).HasColumnName("intent_expected_group_id").ValueGeneratedNever();
            builder.Property(command => command.IntentNewGroupId).HasColumnName("intent_new_group_id").ValueGeneratedNever();
            builder.Property(command => command.ResultGroupId).HasColumnName("result_group_id").ValueGeneratedNever();
            builder.ToTable("product_group_change_commands", table => table.HasCheckConstraint(
                "CK_catalog_product_group_change_commands_result_matches_intent",
                "result_group_id IS NOT DISTINCT FROM intent_new_group_id"));
        }
    }

    private sealed class ProductOperationalNameChangeCommandConfiguration : IEntityTypeConfiguration<ProductOperationalNameChangeCommand>
    {
        public void Configure(EntityTypeBuilder<ProductOperationalNameChangeCommand> builder)
        {
            ConfigureProductCommand(builder, "product_operational_name_change_commands", "PK_catalog_product_operational_name_change_commands", "FK_catalog_product_operational_name_change_commands_products");
            builder.Property(command => command.IntentExpectedOperationalName).HasColumnName("intent_expected_operational_name").HasColumnType("text").IsRequired();
            builder.Property(command => command.IntentNewOperationalName).HasColumnName("intent_new_operational_name").HasColumnType("text").IsRequired();
            builder.Property(command => command.ResultOperationalName).HasColumnName("result_operational_name").HasColumnType("text").IsRequired();
        }
    }

    private sealed class ProductRetireCommandConfiguration : IEntityTypeConfiguration<ProductRetireCommand>
    {
        public void Configure(EntityTypeBuilder<ProductRetireCommand> builder)
        {
            ConfigureProductCommand(builder, "product_retire_commands", "PK_catalog_product_retire_commands", "FK_catalog_product_retire_commands_products");
            builder.Property(command => command.ResultIsActive).HasColumnName("result_is_active").IsRequired();
            builder.Property(command => command.ResultIsAvailable).HasColumnName("result_is_available").IsRequired();
            builder.ToTable("product_retire_commands", table => table.HasCheckConstraint(
                "CK_catalog_product_retire_commands_result", "result_is_active = false"));
        }
    }

    private sealed class ProductReactivateCommandConfiguration : IEntityTypeConfiguration<ProductReactivateCommand>
    {
        public void Configure(EntityTypeBuilder<ProductReactivateCommand> builder)
        {
            ConfigureProductCommand(builder, "product_reactivate_commands", "PK_catalog_product_reactivate_commands", "FK_catalog_product_reactivate_commands_products");
            builder.Property(command => command.ResultIsActive).HasColumnName("result_is_active").IsRequired();
            builder.Property(command => command.ResultIsAvailable).HasColumnName("result_is_available").IsRequired();
            builder.ToTable("product_reactivate_commands", table => table.HasCheckConstraint(
                "CK_catalog_product_reactivate_commands_result", "result_is_active = true AND result_is_available = true"));
        }
    }

    private sealed class ProductAvailabilityChangeCommandConfiguration :
        IEntityTypeConfiguration<ProductAvailabilityChangeCommand>
    {
        public void Configure(EntityTypeBuilder<ProductAvailabilityChangeCommand> builder)
        {
            ConfigureProductCommand(
                builder,
                "product_availability_change_commands",
                "PK_catalog_product_availability_change_commands",
                "FK_catalog_product_availability_change_commands_products");
            builder.Property(command => command.IntentExpectedCurrentAvailability)
                .HasColumnName("intent_expected_current_availability").IsRequired();
            builder.Property(command => command.IntentNewAvailability)
                .HasColumnName("intent_new_availability").IsRequired();
            builder.Property(command => command.ResultAvailability)
                .HasColumnName("result_availability").IsRequired();
            builder.ToTable("product_availability_change_commands", table =>
                table.HasCheckConstraint(
                    "CK_catalog_product_availability_change_commands_result_matches_intent",
                    "result_availability = intent_new_availability"));
        }
    }

    private static void ConfigureProductCommand<T>(EntityTypeBuilder<T> builder, string table, string primaryKey, string foreignKey)
        where T : class
    {
        builder.ToTable(table);
        builder.HasKey("IdempotencyKey").HasName(primaryKey);
        builder.Property<Guid>("IdempotencyKey").HasColumnName("idempotency_key").ValueGeneratedNever();
        builder.Property<Guid?>("ActorIdentityId").HasColumnName("actor_identity_id");
        builder.Property<CatalogCommandKind>("CommandKind").HasColumnName("command_kind").HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property<Guid>("ProductId").HasColumnName("product_id").ValueGeneratedNever();
        builder.HasOne<Product>().WithMany().HasForeignKey("ProductId").HasConstraintName(foreignKey).OnDelete(DeleteBehavior.Restrict);
    }

    private sealed class ProductPreparationConfigurationChangeCommandConfiguration :
        IEntityTypeConfiguration<ProductPreparationConfigurationChangeCommand>
    {
        public void Configure(
            EntityTypeBuilder<ProductPreparationConfigurationChangeCommand> builder)
        {
            builder.ToTable(
                "product_preparation_configuration_change_commands",
                table => table.HasCheckConstraint(
                    "CK_catalog_product_prep_config_cmd_result_matches_intent",
                    "result_responsibility_id IS NOT DISTINCT FROM " +
                    "intent_new_responsibility_id"));
            builder.HasKey(command => command.IdempotencyKey)
                .HasName(
                    "PK_catalog_product_preparation_configuration_change_commands");
            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key").ValueGeneratedNever();
            builder.Property(command => command.ProductId)
                .HasColumnName("product_id").ValueGeneratedNever();
            builder.Property(command => command.IntentExpectedResponsibilityId)
                .HasColumnName("intent_expected_responsibility_id")
                .ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id");
            builder.Property(command => command.CommandKind)
                .HasColumnName("command_kind").HasConversion<string>()
                .HasColumnType("text").IsRequired();
            builder.Property(command => command.IntentNewResponsibilityId)
                .HasColumnName("intent_new_responsibility_id")
                .ValueGeneratedNever();
            builder.Property(command => command.ResultResponsibilityId)
                .HasColumnName("result_responsibility_id")
                .ValueGeneratedNever();
            builder.HasIndex(command => command.ProductId)
                .HasDatabaseName(
                    "IX_catalog_product_prep_config_cmd_product");
            builder.HasOne<Product>().WithMany()
                .HasForeignKey(command => command.ProductId)
                .HasConstraintName(
                    "FK_catalog_product_prep_config_cmd_products")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class ProductCreationCommandConfiguration :
        IEntityTypeConfiguration<ProductCreationCommand>
    {
        public void Configure(EntityTypeBuilder<ProductCreationCommand> builder)
        {
            builder.ToTable(
                "product_creation_commands",
                tableBuilder =>
                {
                    tableBuilder.HasCheckConstraint(
                        "CK_catalog_product_creation_commands_i1_result",
                        "intent_requires_preparation = false AND " +
                        "result_is_active = true AND result_is_available = true");
                    tableBuilder.HasCheckConstraint(
                        "CK_catalog_product_creation_commands_price_non_negative",
                        "intent_price >= 0");
                });

            builder.HasKey(command => command.IdempotencyKey)
                .HasName("PK_catalog_product_creation_commands");

            builder.Property(command => command.IdempotencyKey)
                .HasColumnName("idempotency_key")
                .ValueGeneratedNever();
            builder.Property(command => command.ActorIdentityId)
                .HasColumnName("actor_identity_id");
            builder.Property(command => command.CommandKind)
                .HasColumnName("command_kind").HasConversion<string>()
                .HasColumnType("text").IsRequired();

            builder.Property(command => command.IntentOperationalName)
                .HasColumnName("intent_operational_name")
                .HasColumnType("text")
                .IsRequired();

            builder.Property(command => command.IntentPrice)
                .HasColumnName("intent_price")
                .HasColumnType("numeric")
                .IsRequired();

            builder.Property(command => command.IntentRequiresPreparation)
                .HasColumnName("intent_requires_preparation")
                .IsRequired();

            builder.Property(command => command.ResultProductId)
                .HasColumnName("result_product_id")
                .ValueGeneratedNever();

            builder.Property(command => command.ResultIsActive)
                .HasColumnName("result_is_active")
                .IsRequired();

            builder.Property(command => command.ResultIsAvailable)
                .HasColumnName("result_is_available")
                .IsRequired();

            builder.HasIndex(command => command.ResultProductId)
                .HasDatabaseName("UX_catalog_product_creation_commands_result_product_id")
                .IsUnique();

            builder.HasOne<Product>()
                .WithMany()
                .HasForeignKey(command => command.ResultProductId)
                .HasConstraintName("FK_catalog_product_creation_commands_products")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
