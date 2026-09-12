using Microsoft.AspNetCore.Diagnostics;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.Inventory;
using NexoBar.OperationalConfiguration;
using NexoBar.OrderOperations;
using NexoBar.Host.Notifications;

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

public partial class Program;
