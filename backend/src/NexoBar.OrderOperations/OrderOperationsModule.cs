using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.OrderOperations;

public static class OrderOperationsModule
{
    public static IServiceCollection AddOrderOperations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("OrderOperations")
            ?? throw new InvalidOperationException(
                "Connection string 'OrderOperations' is required.");

        services.AddDbContext<OrderOperationsDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "order_operations")));
        services.AddScoped<FirstConfirmationService>();
        services.AddScoped<OrderQueryService>();

        return services;
    }

    public static IEndpointRouteBuilder MapOrderOperationsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/order-operations/first-confirmations",
                ConfirmFirstAsync)
            .WithName("ConfirmFirstOrderIncorporation")
            .WithTags("OrderOperations")
            .Accepts<FirstConfirmationRequest>("application/json")
            .Produces<FirstConfirmationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapGet(
                "/api/order-operations/orders/{operationalReference}",
                FindOrderAsync)
            .WithName("GetOrderByOperationalReference")
            .WithTags("OrderOperations")
            .Produces<OrderQueryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> FindOrderAsync(
        string operationalReference,
        OrderQueryService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operationalReference, out var orderId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid operational reference",
                "The supplied operational reference is structurally invalid.",
                "order_operations.order.operational_reference_invalid");
        }

        var order = await service.FindAsync(orderId, cancellationToken);

        return order is null
            ? Problem(
                StatusCodes.Status404NotFound,
                "Order not found",
                "No Order exists with the supplied operational reference.",
                "order_operations.order.not_found")
            : Results.Ok(order);
    }

    private static async Task<IResult> ConfirmFirstAsync(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        FirstConfirmationRequest request,
        FirstConfirmationService service,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "First Confirmation requires an Idempotency-Key containing a UUID v4.",
                "order_operations.first_confirmation.idempotency_key_required");
        }

        if (!Guid.TryParse(idempotencyKey, out var commandId) || !IsUuidVersion4(commandId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "order_operations.first_confirmation.idempotency_key_invalid");
        }

        var result = await service.ConfirmAsync(commandId, request, cancellationToken);

        return result.Outcome switch
        {
            FirstConfirmationOutcome.Confirmed => Results.Json(
                result.Response,
                statusCode: StatusCodes.Status201Created),
            FirstConfirmationOutcome.ContextRequired => Problem(
                StatusCodes.Status400BadRequest,
                "Context is required",
                "Context must contain non-whitespace text.",
                "order_operations.first_confirmation.context_required"),
            FirstConfirmationOutcome.CompositionEmpty => Problem(
                StatusCodes.Status400BadRequest,
                "Composition is empty",
                "First Confirmation requires at least one item.",
                "order_operations.first_confirmation.composition_empty"),
            FirstConfirmationOutcome.RequestInvalid => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid First Confirmation request",
                detail: "Every item must contain a non-empty ProductId."),
            FirstConfirmationOutcome.QuantityInvalid => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid quantity",
                "Every quantity must be a positive integer in increment I3.",
                "order_operations.first_confirmation.quantity_invalid",
                result.ProductId),
            FirstConfirmationOutcome.DuplicateProduct => Problem(
                StatusCodes.Status400BadRequest,
                "Duplicate product",
                "A Product may appear only once in a First Confirmation.",
                "order_operations.first_confirmation.duplicate_product",
                result.ProductId),
            FirstConfirmationOutcome.ProductNotCurrent => Problem(
                StatusCodes.Status409Conflict,
                "Product is not current",
                "The Product does not exist or is not active.",
                "order_operations.first_confirmation.product_not_current",
                result.ProductId),
            FirstConfirmationOutcome.ProductUnavailable => Problem(
                StatusCodes.Status409Conflict,
                "Product is unavailable",
                "The Product is not currently available.",
                "order_operations.first_confirmation.product_unavailable",
                result.ProductId),
            FirstConfirmationOutcome.RequiresPreparationNotSupported => Problem(
                StatusCodes.Status409Conflict,
                "Product requires preparation",
                "Products requiring preparation are not supported in increment I3.",
                "order_operations.first_confirmation.requires_preparation_not_supported",
                result.ProductId),
            FirstConfirmationOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible First Confirmation.",
                "order_operations.first_confirmation.idempotency_key_conflict"),
            _ => throw new UnreachableException()
        };
    }

    private static IResult Problem(
        int statusCode,
        string title,
        string detail,
        string code,
        Guid? productId = null)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code
        };

        if (productId is not null)
        {
            extensions["productId"] = productId;
        }

        return Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: extensions);
    }

    private static bool IsUuidVersion4(Guid value)
    {
        var bytes = value.ToByteArray(bigEndian: true);
        var version = bytes[6] >> 4;
        var variant = bytes[8] & 0xc0;

        return version == 4 && variant == 0x80;
    }
}
