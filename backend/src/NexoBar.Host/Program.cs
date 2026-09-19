using Microsoft.AspNetCore.Diagnostics;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.Inventory;
using NexoBar.OperationalConfiguration;
using NexoBar.OrderOperations;
using NexoBar.Host;
using NexoBar.Host.Notifications;

var commandSelection = HostCommandLine.Parse(args);
if (commandSelection.Mode == HostExecutionMode.ProvisionInitialAdmin)
{
    return await HostInitialProvisioningCommand.ExecuteAsync(
        commandSelection,
        Console.In,
        Console.Out,
        Console.IsInputRedirected,
        CancellationToken.None);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddOperationalConfiguration(builder.Configuration);
builder.Services.AddIdentitiesAndCapabilities(builder.Configuration);
builder.Services.AddInventory(builder.Configuration);
builder.Services.AddCatalog(builder.Configuration);
builder.Services.AddOrderOperations(builder.Configuration);
builder.Services.AddSseTransport(builder.Configuration);
builder.Services.AddSingleton<IPreparationDestinationInvalidationPublisher,
    PreparationDestinationInvalidationPublisher>();
builder.Services.AddSingleton<IOrderInvalidationPublisher, OrderInvalidationPublisher>();
builder.Services.AddSingleton<IInventoryOperationInvalidationPublisher,
    InventoryOperationInvalidationPublisher>();

var app = builder.Build();

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception =>
        exception is BadHttpRequestException
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status500InternalServerError
});
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi();
app.MapIdentitySessionEndpoints();
app.MapIdentityAdministrationEndpoints();
app.MapInventoryEndpoints();
app.MapOperationalConfigurationEndpoints();
app.MapCatalogEndpoints();
app.MapOrderOperationsEndpoints();
app.MapSseTransport();

app.Run();

return 0;

public partial class Program;
