using Microsoft.AspNetCore.Diagnostics;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OperationalConfiguration;
using NexoBar.OrderOperations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddOperationalConfiguration(builder.Configuration);
builder.Services.AddIdentitiesAndCapabilities(builder.Configuration);
builder.Services.AddCatalog(builder.Configuration);
builder.Services.AddOrderOperations(builder.Configuration);

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
app.MapOperationalConfigurationEndpoints();
app.MapCatalogEndpoints();
app.MapOrderOperationsEndpoints();

app.Run();

public partial class Program;
