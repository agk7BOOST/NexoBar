using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.Inventory;
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
            ["ConnectionStrings:Inventory"] = connectionString,
            ["ConnectionStrings:OperationalConfiguration"] = connectionString,
            ["ConnectionStrings:OrderOperations"] = connectionString
        })
        .Build();

    var services = new ServiceCollection();
    services.AddOperationalConfiguration(configuration);
    services.AddIdentitiesAndCapabilities(configuration);
    services.AddInventory(configuration);
    services.AddCatalog(configuration);
    services.AddOrderOperations(configuration);

    await using var serviceProvider = services.BuildServiceProvider();
    await using var scope = serviceProvider.CreateAsyncScope();
    var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    var identitiesAndCapabilities = scope.ServiceProvider
        .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
    var operationalConfiguration = scope.ServiceProvider
        .GetRequiredService<OperationalConfigurationDbContext>();
    var inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
    var orderOperations = scope.ServiceProvider
        .GetRequiredService<OrderOperationsDbContext>();

    DbContext[] modularContexts =
    [
        operationalConfiguration,
        identitiesAndCapabilities,
        catalog,
        inventory,
        orderOperations
    ];
    foreach (var context in modularContexts)
    {
        await context.Database.MigrateAsync();
    }

    if (modularContexts.Any(context => context.Database.HasPendingModelChanges()))
    {
        Console.Error.WriteLine(
            "E2E database setup detected model changes without migrations.");
        return 3;
    }

    var kitchen = new PreparationResponsibility(Guid.CreateVersion7(), "Cocina E2E");
    var bar = new PreparationResponsibility(Guid.CreateVersion7(), "Barra E2E");
    operationalConfiguration.PreparationResponsibilities.AddRange(kitchen, bar);
    await operationalConfiguration.SaveChangesAsync();

    var preparer = new Identity("Preparador E2E", true);
    var secondPreparer = new Identity("Preparadora E2E B", true);
    var deliverer = new Identity("Delivery E2E", true);
    var inventoryConfigurator = new Identity("Configurador Inventario E2E", true);
    var inventoryOperator = new Identity("Operador Inventario E2E", true);
    identitiesAndCapabilities.Identities.AddRange(
        preparer,
        secondPreparer,
        deliverer,
        inventoryConfigurator,
        inventoryOperator);
    identitiesAndCapabilities.ResponsibilityAssignments.AddRange(
        new ResponsibilityAssignment(
            preparer.Id,
            FunctionalResponsibility.Preparation),
        new ResponsibilityAssignment(
            secondPreparer.Id,
            FunctionalResponsibility.Preparation),
        new ResponsibilityAssignment(
            deliverer.Id,
            FunctionalResponsibility.OrderOperationsAndBasicClosure),
        new ResponsibilityAssignment(
            inventoryConfigurator.Id,
            FunctionalResponsibility.InventoryConfiguration),
        new ResponsibilityAssignment(
            inventoryOperator.Id,
            FunctionalResponsibility.InventoryOperation));
    identitiesAndCapabilities.PreparationEnablements.AddRange(
        new PreparationEnablement(preparer.Id, kitchen.Id),
        new PreparationEnablement(secondPreparer.Id, kitchen.Id));
    await identitiesAndCapabilities.SaveChangesAsync();
    await scope.ServiceProvider.GetRequiredService<LocalCredentialProvisioner>()
        .ProvisionAsync(
            preparer.Id,
            "preparador-e2e",
            "preparation-e2e-secret",
            CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<LocalCredentialProvisioner>()
        .ProvisionAsync(
            secondPreparer.Id,
            "preparadora-b-e2e",
            "preparation-b-e2e-secret",
            CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<LocalCredentialProvisioner>()
        .ProvisionAsync(
            deliverer.Id,
            "delivery-e2e",
            "delivery-e2e-secret",
            CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<LocalCredentialProvisioner>()
        .ProvisionAsync(
            inventoryConfigurator.Id,
            "inventory-config-e2e",
            "inventory-config-e2e-secret",
            CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<LocalCredentialProvisioner>()
        .ProvisionAsync(
            inventoryOperator.Id,
            "inventory-operation-e2e",
            "inventory-operation-e2e-secret",
            CancellationToken.None);

    var authorizedProduct = new Product(
        Guid.CreateVersion7(),
        "Papas E2E autorizadas",
        7m);
    var otherProduct = new Product(
        Guid.CreateVersion7(),
        "Trago E2E no autorizado",
        9m);
    var directProduct = new Product(
        Guid.CreateVersion7(),
        "Bebida E2E directa",
        5m);
    catalog.Products.AddRange(authorizedProduct, otherProduct, directProduct);
    await catalog.SaveChangesAsync();
    await catalog.Database.ExecuteSqlInterpolatedAsync(
        $"""
        UPDATE catalog.products
        SET requires_preparation = TRUE,
            preparation_responsibility_id = CASE
                WHEN id = {authorizedProduct.Id} THEN {kitchen.Id}
                ELSE {bar.Id}
            END
        WHERE id IN ({authorizedProduct.Id}, {otherProduct.Id})
        """);

    var order = new Order(Guid.CreateVersion7(), "Mesa seguridad E2E");
    var incorporation = new Incorporation(Guid.CreateVersion7(), order.Id, 1);
    orderOperations.Orders.Add(order);
    orderOperations.Incorporations.Add(incorporation);
    orderOperations.IncorporationContents.AddRange(
        new IncorporationContent(
            incorporation.Id,
            1,
            authorizedProduct.Id,
            2,
            true,
            7m,
            "Sin sal"),
        new IncorporationContent(
            incorporation.Id,
            2,
            otherProduct.Id,
            1,
            true,
            9m,
            null),
        new IncorporationContent(
            incorporation.Id,
            3,
            directProduct.Id,
            2,
            false,
            5m,
            null));
    orderOperations.PreparationWork.AddRange(
        new PreparationWork(
            Guid.CreateVersion7(),
            incorporation.Id,
            1,
            kitchen.Id,
            2),
        new PreparationWork(
            Guid.CreateVersion7(),
            incorporation.Id,
            2,
            bar.Id,
            1));
    orderOperations.DeliveryStates.AddRange(
        new DeliveryState(incorporation.Id, 1),
        new DeliveryState(incorporation.Id, 2),
        new DeliveryState(incorporation.Id, 3));
    orderOperations.ConfirmationHistory.Add(new ConfirmationHistory(
        Guid.CreateVersion7(),
        incorporation.Id,
        order.Context,
        deliverer.Id,
        DateTimeOffset.UtcNow));
    await orderOperations.SaveChangesAsync();

    Console.WriteLine(
        $"E2E database migrations and security fixture applied; " +
        $"pending models: {modularContexts.Length}/{modularContexts.Length} false.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"E2E database setup failed: {exception}");
    return 1;
}
