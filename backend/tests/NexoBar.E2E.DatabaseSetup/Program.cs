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
    var priceCatalogConfigurator = new Identity("Catálogo precios E2E", true);
    var interventionOperator = new Identity("Intervención E2E", true);
    // Complete Cancellation's conditional dual authority, without Preparation or enablements.
    var cancellationOperator = new Identity("Cancelación completa E2E", true);
    var inventoryConfigurator = new Identity("Configurador Inventario E2E", true);
    var inventoryOperator = new Identity("Operador Inventario E2E", true);
    identitiesAndCapabilities.Identities.AddRange(
        preparer,
        secondPreparer,
        deliverer,
        priceCatalogConfigurator,
        interventionOperator,
        cancellationOperator,
        inventoryConfigurator,
        inventoryOperator);
    identitiesAndCapabilities.ResponsibilityAssignments.AddRange(
        new ResponsibilityAssignment(
            priceCatalogConfigurator.Id,
            FunctionalResponsibility.CatalogConfiguration),
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
            interventionOperator.Id,
            FunctionalResponsibility.OperationalIntervention),
        new ResponsibilityAssignment(
            cancellationOperator.Id,
            FunctionalResponsibility.OrderOperationsAndBasicClosure),
        new ResponsibilityAssignment(
            cancellationOperator.Id,
            FunctionalResponsibility.OperationalIntervention),
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
            priceCatalogConfigurator.Id,
            "price-catalog-e2e",
            "price-catalog-e2e-secret",
            CancellationToken.None);
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
            interventionOperator.Id,
            "intervention-e2e",
            "intervention-e2e-secret",
            CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<LocalCredentialProvisioner>()
        .ProvisionAsync(
            cancellationOperator.Id,
            "complete-cancellation-e2e",
            "complete-cancellation-e2e-secret",
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
    var interventionProduct = new Product(
        Guid.CreateVersion7(),
        "Papas E2E intervención",
        7m);
    catalog.Products.AddRange(authorizedProduct, otherProduct, directProduct, interventionProduct);
    await catalog.SaveChangesAsync();
    await catalog.Database.ExecuteSqlInterpolatedAsync(
        $"""
        UPDATE catalog.products
        SET requires_preparation = TRUE,
            preparation_responsibility_id = CASE
                WHEN id IN ({authorizedProduct.Id}, {interventionProduct.Id}) THEN {kitchen.Id}
                ELSE {bar.Id}
            END
        WHERE id IN ({authorizedProduct.Id}, {otherProduct.Id}, {interventionProduct.Id})
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
    orderOperations.ContentQuantityStates.AddRange(
        new ContentQuantityState(incorporation.Id, 1),
        new ContentQuantityState(incorporation.Id, 2),
        new ContentQuantityState(incorporation.Id, 3));
    orderOperations.ConfirmationHistory.Add(new ConfirmationHistory(
        Guid.CreateVersion7(),
        incorporation.Id,
        order.Context,
        deliverer.Id,
        DateTimeOffset.UtcNow));
    await orderOperations.SaveChangesAsync();

    // Dedicated single-content Order for the OperationalIntervention scenario.
    var interventionOrder = new Order(Guid.CreateVersion7(), "Mesa intervención E2E");
    var interventionIncorporation = new Incorporation(
        Guid.CreateVersion7(), interventionOrder.Id, 1);
    orderOperations.Orders.Add(interventionOrder);
    orderOperations.Incorporations.Add(interventionIncorporation);
    orderOperations.IncorporationContents.Add(new IncorporationContent(
        interventionIncorporation.Id,
        1,
        interventionProduct.Id,
        2,
        true,
        7m,
        "Sin sal"));
    orderOperations.PreparationWork.Add(new PreparationWork(
        Guid.CreateVersion7(), interventionIncorporation.Id, 1, kitchen.Id, 2));
    orderOperations.DeliveryStates.Add(new DeliveryState(interventionIncorporation.Id, 1));
    orderOperations.ContentQuantityStates.Add(new ContentQuantityState(interventionIncorporation.Id, 1));
    orderOperations.ConfirmationHistory.Add(new ConfirmationHistory(
        Guid.CreateVersion7(),
        interventionIncorporation.Id,
        interventionOrder.Context,
        deliverer.Id,
        DateTimeOffset.UtcNow));
    await orderOperations.SaveChangesAsync();

    // Dedicated single-destination fixture for the multi-session SSE checkpoint.
    var sseDestination = new PreparationResponsibility(Guid.CreateVersion7(), "Cocina SSE E2E");
    operationalConfiguration.PreparationResponsibilities.Add(sseDestination);
    await operationalConfiguration.SaveChangesAsync();
    foreach (var suffix in new[] { "a", "b" })
    {
        var sseOperator = new Identity($"Preparador SSE {suffix.ToUpperInvariant()} E2E", true);
        identitiesAndCapabilities.Identities.Add(sseOperator);
        identitiesAndCapabilities.ResponsibilityAssignments.Add(new ResponsibilityAssignment(
            sseOperator.Id, FunctionalResponsibility.Preparation));
        identitiesAndCapabilities.PreparationEnablements.Add(new PreparationEnablement(
            sseOperator.Id, sseDestination.Id));
        await identitiesAndCapabilities.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<LocalCredentialProvisioner>()
            .ProvisionAsync(sseOperator.Id, $"preparation-sse-{suffix}-e2e",
                $"preparation-sse-{suffix}-e2e-secret", CancellationToken.None);
    }

    var sseProduct = new Product(Guid.CreateVersion7(), "Papas SSE E2E", 7m);
    catalog.Products.Add(sseProduct);
    await catalog.SaveChangesAsync();
    await catalog.Database.ExecuteSqlInterpolatedAsync($"""
        UPDATE catalog.products SET requires_preparation = TRUE,
            preparation_responsibility_id = {sseDestination.Id} WHERE id = {sseProduct.Id}
        """);
    var sseOrder = new Order(Guid.CreateVersion7(), "Mesa SSE E2E");
    var sseIncorporation = new Incorporation(Guid.CreateVersion7(), sseOrder.Id, 1);
    orderOperations.Orders.Add(sseOrder);
    orderOperations.Incorporations.Add(sseIncorporation);
    orderOperations.IncorporationContents.Add(new IncorporationContent(
        sseIncorporation.Id, 1, sseProduct.Id, 1, true, 7m, null));
    orderOperations.PreparationWork.Add(new PreparationWork(
        Guid.CreateVersion7(), sseIncorporation.Id, 1, sseDestination.Id, 1));
    orderOperations.DeliveryStates.Add(new DeliveryState(sseIncorporation.Id, 1));
    orderOperations.ContentQuantityStates.Add(new ContentQuantityState(sseIncorporation.Id, 1));
    orderOperations.ConfirmationHistory.Add(new ConfirmationHistory(
        Guid.CreateVersion7(), sseIncorporation.Id, sseOrder.Context, deliverer.Id, DateTimeOffset.UtcNow));
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
