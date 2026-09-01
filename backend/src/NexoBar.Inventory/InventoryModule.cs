using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.Inventory;

public static class InventoryModule
{
    public static IServiceCollection AddInventory(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Inventory")
            ?? throw new InvalidOperationException(
                "Connection string 'Inventory' is required.");

        services.AddDbContext<InventoryDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "inventory")));
        services.AddScoped<InventoryService>();
        return services;
    }

    public static IEndpointRouteBuilder MapInventoryEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/inventory/items", CreateItemAsync)
            .WithName("CreateInventoryItem")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Accepts<CreateInventoryItemRequest>("application/json")
            .Produces<InventoryItemResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapGet(
                "/api/inventory/configuration/items",
                ListConfigurationItemsAsync)
            .WithName("ListInventoryConfigurationItems")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Produces<IReadOnlyList<InventoryConfigurationItemResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(
                "/api/inventory/operations/items",
                ListOperationalItemsAsync)
            .WithName("ListInventoryOperationalItems")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Produces<IReadOnlyList<InventoryOperationalItemResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> CreateItemAsync(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        CreateInventoryItemRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        InventoryService service,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Inventory Item creation requires an Idempotency-Key containing a UUID v4.",
                "inventory.item.idempotency_key_required");
        }

        if (!Guid.TryParse(idempotencyKey, out var commandId) ||
            !IsUuidVersion4(commandId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "inventory.item.idempotency_key_invalid");
        }

        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Antiforgery validation failed",
                "A valid antiforgery cookie and request token are required.",
                "inventory.item.antiforgery_invalid");
        }

        var result = await service.CreateItemAsync(
            commandId,
            request,
            cancellationToken);
        return result.Outcome switch
        {
            CreateInventoryItemOutcome.Created => Results.Created(
                $"/api/inventory/items/{result.Item!.ItemId}",
                result.Item),
            CreateInventoryItemOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            CreateInventoryItemOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Inventory configuration forbidden",
                "The current Identity is not authorized to configure Inventory.",
                "inventory.configuration.forbidden"),
            CreateInventoryItemOutcome.InvalidName => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid operational name",
                "operationalName must be non-empty, one line, and at most 200 characters.",
                "inventory.item.operational_name_invalid"),
            CreateInventoryItemOutcome.InvalidUnit => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid operational unit",
                "operationalUnit must be non-empty, one line, and at most 100 characters.",
                "inventory.item.operational_unit_invalid"),
            CreateInventoryItemOutcome.DuplicateName => Problem(
                StatusCodes.Status409Conflict,
                "Operational name already in use",
                "An Inventory Item already uses that operational name, ignoring case.",
                "inventory.item.operational_name_conflict"),
            CreateInventoryItemOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Inventory Item creation.",
                "inventory.item.idempotency_key_conflict"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ListConfigurationItemsAsync(
        InventoryService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListConfigurationItemsAsync(cancellationToken);
        return MapReadResult(
            result,
            "Inventory configuration forbidden",
            "The current Identity is not authorized to configure Inventory.",
            "inventory.configuration.forbidden");
    }

    private static async Task<IResult> ListOperationalItemsAsync(
        InventoryService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListOperationalItemsAsync(cancellationToken);
        return MapReadResult(
            result,
            "Inventory operation forbidden",
            "The current Identity is not authorized to operate Inventory.",
            "inventory.operation.forbidden");
    }

    private static IResult MapReadResult<TResponse>(
        InventoryReadResult<TResponse> result,
        string forbiddenTitle,
        string forbiddenDetail,
        string forbiddenCode) =>
        result.Outcome switch
        {
            InventoryReadOutcome.Succeeded => Results.Ok(result.Items),
            InventoryReadOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            InventoryReadOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                forbiddenTitle,
                forbiddenDetail,
                forbiddenCode),
            _ => throw new UnreachableException()
        };

    private static IResult Problem(
        int statusCode,
        string title,
        string detail,
        string code) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static bool IsUuidVersion4(Guid value)
    {
        var bytes = value.ToByteArray(bigEndian: true);
        return bytes[6] >> 4 == 4 && (bytes[8] & 0xc0) == 0x80;
    }
}
