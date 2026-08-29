using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexoBar.Catalog;

internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    internal DbSet<Product> Products => Set<Product>();

    internal DbSet<ProductCreationCommand> ProductCreationCommands =>
        Set<ProductCreationCommand>();

    internal DbSet<ProductPriceChangeCommand> ProductPriceChangeCommands =>
        Set<ProductPriceChangeCommand>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");
        modelBuilder.ApplyConfiguration(new ProductConfiguration());
        modelBuilder.ApplyConfiguration(new ProductCreationCommandConfiguration());
        modelBuilder.ApplyConfiguration(new ProductPriceChangeCommandConfiguration());
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
                        "CK_catalog_products_requires_preparation_i1",
                        "requires_preparation = false");
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

            builder.HasIndex(product => product.NormalizedOperationalName)
                .HasDatabaseName("UX_catalog_products_active_normalized_operational_name")
                .IsUnique()
                .HasFilter("is_active");
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
