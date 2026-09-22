using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.Inventory;
using NexoBar.OperationalConfiguration;
using NexoBar.OrderOperations;

namespace NexoBar.Migrations;

public sealed record MigrationStep(
    string Module,
    Func<IServiceProvider, CancellationToken, Task> ApplyAsync,
    Func<IServiceProvider, CancellationToken, Task<IReadOnlyList<string>>> ReadAppliedMigrationsAsync,
    Func<IServiceProvider, bool> HasPendingModelChanges);

public static class MigrationPlan
{
    // Keep this topological order in one place. Runtime/module dependencies:
    // OperationalConfiguration -> Identity/Catalog; Identity -> Inventory;
    // Catalog + Identity -> OrderOperations. The latter edge also supports
    // future OrderOperations migration-time reads of configured Context state.
    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        Create<OperationalConfigurationDbContext>("OperationalConfiguration"),
        Create<IdentitiesAndCapabilitiesDbContext>("IdentitiesAndCapabilities"),
        Create<CatalogDbContext>("Catalog"),
        Create<InventoryDbContext>("Inventory"),
        Create<OrderOperationsDbContext>("OrderOperations")
    ];

    private static MigrationStep Create<TContext>(string module)
        where TContext : DbContext => new(
            module,
            async (services, cancellationToken) =>
                await services.GetRequiredService<TContext>()
                    .Database.MigrateAsync(cancellationToken),
            async (services, cancellationToken) =>
                (await services.GetRequiredService<TContext>()
                    .Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray(),
            services => services.GetRequiredService<TContext>()
                .Database.HasPendingModelChanges());
}
