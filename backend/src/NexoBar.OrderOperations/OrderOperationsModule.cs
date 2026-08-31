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
        services.AddScoped<SubsequentConfirmationService>();
        services.AddScoped<OrderQueryService>();
        services.AddScoped<PreparationWorkQueryService>();
        services.AddScoped<PreparationStartService>();

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
                "/api/order-operations/preparation/work",
                ListPreparationWorkAsync)
            .WithName("ListPreparationWork")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Produces<IReadOnlyList<PreparationWorkResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        endpoints.MapPost(
                "/api/order-operations/preparation/work/{workId}/start",
                StartPreparationQuantityAsync)
            .WithName("StartPreparationQuantity")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Accepts<StartPreparationQuantityRequest>("application/json")
            .Produces<StartPreparationQuantityResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapGet(
                "/api/order-operations/orders/{operationalReference}",
                FindOrderAsync)
            .WithName("GetOrderByOperationalReference")
            .WithTags("OrderOperations")
            .Produces<OrderQueryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost(
                "/api/order-operations/orders/{operationalReference}/confirmations",
                ConfirmSubsequentAsync)
            .WithName("ConfirmSubsequentOrderIncorporation")
            .WithTags("OrderOperations")
            .Accepts<SubsequentConfirmationRequest>("application/json")
            .Produces<SubsequentConfirmationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> StartPreparationQuantityAsync(
        string workId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        StartPreparationQuantityRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        PreparationStartService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workId, out var parsedWorkId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Preparation Work",
                "workId must contain a UUID.",
                "order_operations.preparation_start.work_id_invalid");
        }

        if (idempotencyKey is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Starting Preparation requires an Idempotency-Key containing a UUID v4.",
                "order_operations.preparation_start.idempotency_key_required");
        }

        if (!Guid.TryParse(idempotencyKey, out var commandId) ||
            !IsUuidVersion4(commandId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "order_operations.preparation_start.idempotency_key_invalid");
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
                "order_operations.preparation_start.antiforgery_invalid");
        }

        var result = await service.StartAsync(
            commandId,
            parsedWorkId,
            request.Quantity,
            cancellationToken);
        return result.Outcome switch
        {
            PreparationStartOutcome.Started => Results.Ok(result.Response),
            PreparationStartOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            PreparationStartOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Preparation access forbidden",
                "The current Identity is not authorized for Preparation.",
                "order_operations.preparation.forbidden"),
            PreparationStartOutcome.WorkNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Preparation Work not found",
                "No accessible Preparation Work exists with the supplied identifier.",
                "order_operations.preparation_work.not_found"),
            PreparationStartOutcome.QuantityInvalid => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid quantity",
                "quantity must be a positive integer.",
                "order_operations.preparation_start.quantity_invalid"),
            PreparationStartOutcome.PendingQuantityInsufficient => Problem(
                StatusCodes.Status409Conflict,
                "Pending quantity is insufficient",
                "The requested quantity is greater than the Work quantity currently Pending.",
                "order_operations.preparation_start.pending_quantity_insufficient"),
            PreparationStartOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Preparation command.",
                "order_operations.preparation_start.idempotency_key_conflict"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ConfirmSubsequentAsync(
        string operationalReference,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        SubsequentConfirmationRequest request,
        SubsequentConfirmationService service,
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

        if (idempotencyKey is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Subsequent Confirmation requires an Idempotency-Key containing a UUID v4.",
                "order_operations.subsequent_confirmation.idempotency_key_required");
        }

        if (!Guid.TryParse(idempotencyKey, out var commandId) || !IsUuidVersion4(commandId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "order_operations.subsequent_confirmation.idempotency_key_invalid");
        }

        var result = await service.ConfirmAsync(
            commandId,
            orderId,
            request,
            cancellationToken);

        return result.Outcome switch
        {
            SubsequentConfirmationOutcome.Confirmed => Results.Json(
                result.Response,
                statusCode: StatusCodes.Status201Created),
            SubsequentConfirmationOutcome.CompositionEmpty => Problem(
                StatusCodes.Status400BadRequest,
                "Composition is empty",
                "Confirmation requires at least one item.",
                "order_operations.first_confirmation.composition_empty"),
            SubsequentConfirmationOutcome.RequestInvalid => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Confirmation request",
                detail: "Every item must contain a non-empty ProductId."),
            SubsequentConfirmationOutcome.QuantityInvalid => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid quantity",
                "Every quantity must be a positive integer in increment I3.",
                "order_operations.first_confirmation.quantity_invalid",
                result.ProductId),
            SubsequentConfirmationOutcome.DuplicateLine => Problem(
                StatusCodes.Status400BadRequest,
                "Duplicate confirmed line",
                "A Product and canonical Instruction pair may appear only once in a Confirmation.",
                "order_operations.confirmation.duplicate_line",
                result.ProductId),
            SubsequentConfirmationOutcome.OrderNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Order not found",
                "No Order exists with the supplied operational reference.",
                "order_operations.order.not_found"),
            SubsequentConfirmationOutcome.ProductNotCurrent => Problem(
                StatusCodes.Status409Conflict,
                "Product is not current",
                "The Product does not exist or is not active.",
                "order_operations.first_confirmation.product_not_current",
                result.ProductId),
            SubsequentConfirmationOutcome.ProductUnavailable => Problem(
                StatusCodes.Status409Conflict,
                "Product is unavailable",
                "The Product is not currently available.",
                "order_operations.first_confirmation.product_unavailable",
                result.ProductId),
            SubsequentConfirmationOutcome.InstructionRequiresPreparation => Problem(
                StatusCodes.Status409Conflict,
                "Instruction requires preparation",
                "A non-prepared Product cannot be confirmed with an Instruction.",
                "order_operations.confirmation.instruction_requires_preparation",
                result.ProductId),
            SubsequentConfirmationOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible subsequent Confirmation.",
                "order_operations.subsequent_confirmation.idempotency_key_conflict"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ListPreparationWorkAsync(
        [FromQuery(Name = "preparationResponsibilityId"), Required]
        string? preparationResponsibilityId,
        PreparationWorkQueryService service,
        CancellationToken cancellationToken)
    {
        if (preparationResponsibilityId is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Preparation Responsibility is required",
                "Preparation work lookup requires a preparationResponsibilityId query parameter.",
                "order_operations.preparation_work.responsibility_id_required");
        }

        if (!Guid.TryParse(preparationResponsibilityId, out var responsibilityId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Preparation Responsibility",
                "preparationResponsibilityId must contain a UUID.",
                "order_operations.preparation_work.responsibility_id_invalid");
        }

        var result = await service.ListAsync(responsibilityId, cancellationToken);
        return result.Outcome switch
        {
            PreparationWorkQueryOutcome.Succeeded => Results.Ok(result.Work),
            PreparationWorkQueryOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            PreparationWorkQueryOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Preparation work access forbidden",
                "The current Identity is not authorized for the requested Preparation destination.",
                "order_operations.preparation.forbidden"),
            PreparationWorkQueryOutcome.ProductReferenceInconsistent => Problem(
                StatusCodes.Status500InternalServerError,
                "Product reference is inconsistent",
                "Preparation Work references a Product that Catalog cannot resolve.",
                "order_operations.preparation_work.product_reference_inconsistent"),
            _ => throw new UnreachableException()
        };
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
            FirstConfirmationOutcome.DuplicateLine => Problem(
                StatusCodes.Status400BadRequest,
                "Duplicate confirmed line",
                "A Product and canonical Instruction pair may appear only once in a Confirmation.",
                "order_operations.confirmation.duplicate_line",
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
            FirstConfirmationOutcome.InstructionRequiresPreparation => Problem(
                StatusCodes.Status409Conflict,
                "Instruction requires preparation",
                "A non-prepared Product cannot be confirmed with an Instruction.",
                "order_operations.confirmation.instruction_requires_preparation",
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
