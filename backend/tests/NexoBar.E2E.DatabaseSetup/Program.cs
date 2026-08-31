using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OperationalConfiguration;
using NexoBar.OrderOperations;

const string ConnectionStringEnvironmentVariable =
    "NEXOBAR_E2E_CONNECTION_STRING";

var connectionString = Environment.GetEnvironmentVariable(
    ConnectionStringEnvironmentVariable);

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        $"E2E database setup requires {ConnectionStringEnvironmentVariable}.");
    return 2;
}

try
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Catalog"] = connectionString,
            ["ConnectionStrings:IdentitiesAndCapabilities"] = connectionString,
            ["ConnectionStrings:OperationalConfiguration"] = connectionString,
            ["ConnectionStrings:OrderOperations"] = connectionString
        })
        .Build();

    var services = new ServiceCollection();
    services.AddOperationalConfiguration(configuration);
    services.AddIdentitiesAndCapabilities(configuration);
    services.AddCatalog(configuration);
    services.AddOrderOperations(configuration);

    await using var serviceProvider = services.BuildServiceProvider();
    await using var scope = serviceProvider.CreateAsyncScope();
    var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    var identitiesAndCapabilities = scope.ServiceProvider
        .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
    var operationalConfiguration = scope.ServiceProvider
        .GetRequiredService<OperationalConfigurationDbContext>();
    var orderOperations = scope.ServiceProvider
        .GetRequiredService<OrderOperationsDbContext>();

    await operationalConfiguration.Database.MigrateAsync();
    await identitiesAndCapabilities.Database.MigrateAsync();
    await catalog.Database.MigrateAsync();
    await orderOperations.Database.MigrateAsync();

    if (identitiesAndCapabilities.Database.HasPendingModelChanges() ||
        operationalConfiguration.Database.HasPendingModelChanges() ||
        catalog.Database.HasPendingModelChanges() ||
        orderOperations.Database.HasPendingModelChanges())
    {
        Console.Error.WriteLine(
            "E2E database setup detected model changes without migrations.");
        return 3;
    }

    Console.WriteLine(
        "E2E database migrations applied; no pending model changes detected.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"E2E database setup failed: {exception}");
    return 1;
}
