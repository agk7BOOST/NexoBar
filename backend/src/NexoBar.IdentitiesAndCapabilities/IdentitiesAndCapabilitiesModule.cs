using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.IdentitiesAndCapabilities;

public static class IdentitiesAndCapabilitiesModule
{
    public static IServiceCollection AddIdentitiesAndCapabilities(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(
            "IdentitiesAndCapabilities")
            ?? throw new InvalidOperationException(
                "Connection string 'IdentitiesAndCapabilities' is required.");

        services.AddDbContext<IdentitiesAndCapabilitiesDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "identities_and_capabilities")));
        services.AddScoped<PreparationEnablementService>();

        return services;
    }
}
