using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.Inventory;
using NexoBar.OperationalConfiguration;
using NexoBar.OrderOperations;

namespace NexoBar.Migrations;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environments.Production
        });
        using var loggerFactory = LoggerFactory.Create(logging =>
            logging.AddConfiguration(builder.Configuration.GetSection("Logging"))
                .AddConsole());
        var logger = loggerFactory.CreateLogger("NexoBar.Migrations");

        try
        {
            await using var services = MigrationServices.Build(builder.Configuration);
            return await MigrationOrchestrator.RunAsync(
                MigrationPlan.Steps,
                services,
                logger,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Migration runner could not initialize ({ExceptionType}).",
                exception.GetType().Name);
            return 1;
        }
    }
}

public static class MigrationServices
{
    public static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperationalConfiguration(configuration);
        services.AddIdentitiesAndCapabilities(configuration);
        services.AddCatalog(configuration);
        services.AddInventory(configuration);
        services.AddOrderOperations(configuration);
        return services.BuildServiceProvider();
    }
}
