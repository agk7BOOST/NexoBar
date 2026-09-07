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

public static partial class OrderOperationsModule
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
        services.AddScoped<PendingCompositionService>();
        services.AddScoped<OrderQueryService>();
        services.AddScoped<OrderEconomicStateReader>();
        services.AddScoped<LiquidationService>();
        services.AddScoped<ClosureService>();
        services.AddScoped<ClosureStateReader>();
        services.AddScoped<OrderDeliveryQueryService>();
        services.AddScoped<DeliveryQuantityService>();
        services.AddScoped<DeliveryCorrectionService>();
        services.AddScoped<ContentCorrectionService>();
        services.AddScoped<ContentCancellationService>();
        services.AddScoped<PreparationWorkQueryService>();
        services.AddScoped<PreparationProgressService>();

        return services;
    }

    public static IEndpointRouteBuilder MapOrderOperationsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/order-operations/orders/{orderId}/incorporations/{incorporationId}/contents/{contentOrdinal}/cancel-content-quantity", CancelContentAsync)
            .WithName("CancelContentQuantity")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Accepts<CancelContentRequest>("application/json")
            .Produces<ContentCancellationResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(500);
        endpoints.MapPost("/api/order-operations/orders/{orderId}/incorporations/{incorporationId}/contents/{contentOrdinal}/correct-content-quantity", CorrectContentAsync)
            .WithName("CorrectContentQuantity")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Accepts<CorrectContentRequest>("application/json")
            .Produces<ContentCorrectionResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(500);
        endpoints.MapPost("/api/order-operations/orders/{orderId}/incorporations/{incorporationId}/contents/{contentOrdinal}/correct-delivery", CorrectDeliveryAsync)
            .WithName("CorrectDeliveryQuantity")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Accepts<CorrectDeliveryRequest>("application/json")
            .Produces<DeliveryCorrectionResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(500);
        endpoints.MapPost(
                "/api/order-operations/first-confirmations",
                ConfirmFirstAsync)
            .WithName("ConfirmFirstOrderIncorporation")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Accepts<FirstConfirmationRequest>("application/json")
            .Produces<FirstConfirmationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
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

        endpoints.MapPost(
                "/api/order-operations/preparation/work/{workId}/ready",
                MarkPreparationQuantityReadyAsync)
            .WithName("MarkPreparationQuantityReady")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Accepts<MarkPreparationQuantityReadyRequest>("application/json")
            .Produces<MarkPreparationQuantityReadyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapPost(
                "/api/order-operations/incorporations/{incorporationId}/contents/{contentOrdinal}/deliver",
                DeliverQuantityAsync)
            .WithName("DeliverQuantity")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Accepts<DeliverQuantityRequest>("application/json")
            .Produces<DeliverQuantityResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        endpoints.MapGet(
                "/api/order-operations/orders/{operationalReference}",
                FindOrderAsync)
            .WithName("GetOrderByOperationalReference")
            .WithTags("OrderOperations")
            .Produces<OrderQueryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(
                "/api/order-operations/orders/{operationalReference}/delivery",
                FindOrderDeliveryAsync)
            .WithName("GetOrderDeliveryByOperationalReference")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Produces<OrderDeliveryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        endpoints.MapPost(
                "/api/order-operations/orders/{operationalReference}/confirmations",
                ConfirmSubsequentAsync)
            .WithName("ConfirmSubsequentOrderIncorporation")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Accepts<SubsequentConfirmationRequest>("application/json")
            .Produces<SubsequentConfirmationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapPost(
                "/api/order-operations/orders/{operationalReference}/liquidate-simple",
                LiquidateSimpleAsync)
            .WithName("LiquidateSimple")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Accepts<LiquidateSimpleRequest>("application/json")
            .Produces<LiquidationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        endpoints.MapPost(
                "/api/order-operations/orders/{operationalReference}/record-external-collection",
                RecordExternalCollectionAsync)
            .WithName("RecordExternalCollection")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Produces<LiquidationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        endpoints.MapPost(
                "/api/orders/{orderId}/pending-composition",
                StartPendingCompositionAsync)
            .WithName("StartPendingComposition")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Produces<PendingCompositionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapGet(
                "/api/orders/{orderId}/pending-composition",
                FindPendingCompositionAsync)
            .WithName("GetPendingComposition")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Produces<CurrentPendingCompositionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost(
                "/api/orders/{orderId}/pending-composition/{pendingCompositionId}/discard",
                DiscardPendingCompositionAsync)
            .WithName("DiscardPendingComposition")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapPost("/api/orders/{orderId}/close", CloseOrderAsync)
            .WithName("CloseOrder")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Produces<ClosureResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> LiquidateSimpleAsync(
        string operationalReference,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        LiquidateSimpleRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        LiquidationService service,
        CancellationToken cancellationToken)
    {
        var validation = ValidateLiquidationCommand(
            operationalReference,
            idempotencyKey);
        if (validation.Error is not null)
        {
            return validation.Error;
        }
        var antiforgeryError = await ValidateLiquidationAntiforgeryAsync(
            httpContext,
            antiforgery);
        if (antiforgeryError is not null)
        {
            return antiforgeryError;
        }

        var medium = LiquidationService.CanonicalizeDeclaredPaymentMedium(
            request.DeclaredPaymentMedium);
        if (medium is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid declared payment medium",
                $"declaredPaymentMedium must contain 1 to {LiquidationService.DeclaredPaymentMediumMaxLength} characters after trimming outer whitespace.",
                "order_operations.liquidation.declared_payment_medium_invalid");
        }

        return ToLiquidationHttpResult(await service.LiquidateSimpleAsync(
            validation.IdempotencyKey,
            validation.OrderId,
            medium,
            cancellationToken));
    }

    private static async Task<IResult> RecordExternalCollectionAsync(
        string operationalReference,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        LiquidationService service,
        CancellationToken cancellationToken)
    {
        var validation = ValidateLiquidationCommand(
            operationalReference,
            idempotencyKey);
        if (validation.Error is not null)
        {
            return validation.Error;
        }
        if (httpContext.Request.ContentLength is > 0 ||
            httpContext.Request.Headers.TransferEncoding.Count > 0)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Request body is not accepted",
                "RecordExternalCollection does not accept payment-medium or external financial details.",
                "order_operations.liquidation.external_collection_body_not_allowed");
        }
        var antiforgeryError = await ValidateLiquidationAntiforgeryAsync(
            httpContext,
            antiforgery);
        if (antiforgeryError is not null)
        {
            return antiforgeryError;
        }

        return ToLiquidationHttpResult(await service.RecordExternalCollectionAsync(
            validation.IdempotencyKey,
            validation.OrderId,
            cancellationToken));
    }

    private static LiquidationCommandValidation ValidateLiquidationCommand(
        string operationalReference,
        string? idempotencyKey)
    {
        if (!Guid.TryParse(operationalReference, out var orderId))
        {
            return LiquidationCommandValidation.Invalid(Problem(
                StatusCodes.Status400BadRequest,
                "Invalid operational reference",
                "The supplied operational reference is structurally invalid.",
                "order_operations.order.operational_reference_invalid"));
        }

        if (idempotencyKey is null)
        {
            return LiquidationCommandValidation.Invalid(Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Liquidation requires an Idempotency-Key containing a UUID v4.",
                "order_operations.liquidation.idempotency_key_required"));
        }

        if (!Guid.TryParse(idempotencyKey, out var commandId) || !IsUuidVersion4(commandId))
        {
            return LiquidationCommandValidation.Invalid(Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "order_operations.liquidation.idempotency_key_invalid"));
        }

        return LiquidationCommandValidation.Valid(orderId, commandId);
    }

    private static async Task<IResult?> ValidateLiquidationAntiforgeryAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Antiforgery validation failed",
                "A valid antiforgery cookie and request token are required.",
                "order_operations.liquidation.antiforgery_invalid");
        }
    }

    private static IResult ToLiquidationHttpResult(LiquidationResult result) =>
        result.Outcome switch
        {
            LiquidationOutcome.Succeeded => Results.Ok(ToLiquidationResponse(result.Response!)),
            LiquidationOutcome.Unauthenticated => InvalidSession(),
            LiquidationOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Liquidation forbidden",
                "The current Identity is not authorized for Order Operations and basic Closure.",
                "order_operations.liquidation.forbidden"),
            LiquidationOutcome.OrderNotFound => PendingCompositionOrderNotFound(),
            LiquidationOutcome.OrderFrozen => FrozenOrderProblem(),
            LiquidationOutcome.PendingComposition => Problem(
                StatusCodes.Status409Conflict,
                "Pending Composition blocks Liquidation",
                "The Order has a current Pending Composition.",
                "order_operations.liquidation.pending_composition"),
            LiquidationOutcome.UnresolvedFulfillment => Problem(
                StatusCodes.Status409Conflict,
                "Fulfillment blocks Liquidation",
                "The Order contains current content that is not fully delivered.",
                "order_operations.liquidation.unresolved_fulfillment"),
            LiquidationOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Liquidation command.",
                "order_operations.liquidation.idempotency_key_conflict"),
            LiquidationOutcome.StateInconsistent => Problem(
                StatusCodes.Status500InternalServerError,
                "Order economic state is inconsistent",
                "The Order cannot be resolved economically from its current authoritative State.",
                "order_operations.liquidation.state_inconsistent"),
            _ => throw new UnreachableException()
        };

    private static LiquidationResponse ToLiquidationResponse(LiquidationCommandResult result) =>
        new(
            result.LiquidationId,
            result.OrderId,
            result.Mode,
            result.FunctionalAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.DeclaredPaymentMedium,
            result.OccurredAt,
            IsFrozen: true);

    private static IResult FrozenOrderProblem() => Problem(
        StatusCodes.Status409Conflict,
        "Order is frozen",
        "The Order is frozen by its successful Liquidation.",
        "order_operations.order.frozen");

    private sealed record LiquidationCommandValidation(
        Guid OrderId,
        Guid IdempotencyKey,
        IResult? Error)
    {
        internal static LiquidationCommandValidation Valid(
            Guid orderId,
            Guid idempotencyKey) => new(orderId, idempotencyKey, null);

        internal static LiquidationCommandValidation Invalid(IResult error) =>
            new(Guid.Empty, Guid.Empty, error);
    }

    private static async Task<IResult> StartPreparationQuantityAsync(
        string workId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        StartPreparationQuantityRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        PreparationProgressService service,
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
            PreparationProgressOutcome.Succeeded => Results.Ok(
                ToStartResponse(result.Response!)),
            PreparationProgressOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            PreparationProgressOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Preparation access forbidden",
                "The current Identity is not authorized for Preparation.",
                "order_operations.preparation.forbidden"),
            PreparationProgressOutcome.WorkNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Preparation Work not found",
                "No accessible Preparation Work exists with the supplied identifier.",
                "order_operations.preparation_work.not_found"),
            PreparationProgressOutcome.QuantityInvalid => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid quantity",
                "quantity must be a positive integer.",
                "order_operations.preparation_start.quantity_invalid"),
            PreparationProgressOutcome.AvailableQuantityInsufficient => Problem(
                StatusCodes.Status409Conflict,
                "Pending quantity is insufficient",
                "The requested quantity is greater than the Work quantity currently Pending.",
                "order_operations.preparation_start.pending_quantity_insufficient"),
            PreparationProgressOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Preparation command.",
                "order_operations.preparation_start.idempotency_key_conflict"),
            PreparationProgressOutcome.OrderFrozen => FrozenOrderProblem(),
            PreparationProgressOutcome.StateInconsistent => Problem(
                StatusCodes.Status500InternalServerError,
                "Preparation state is inconsistent",
                "The target Work cannot be mutated because its current State is inconsistent.",
                "order_operations.preparation.state_inconsistent"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> MarkPreparationQuantityReadyAsync(
        string workId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        MarkPreparationQuantityReadyRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        PreparationProgressService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workId, out var parsedWorkId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Preparation Work",
                "workId must contain a UUID.",
                "order_operations.preparation_ready.work_id_invalid");
        }

        if (idempotencyKey is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Marking Preparation Ready requires an Idempotency-Key containing a UUID v4.",
                "order_operations.preparation_ready.idempotency_key_required");
        }

        if (!Guid.TryParse(idempotencyKey, out var commandId) ||
            !IsUuidVersion4(commandId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "order_operations.preparation_ready.idempotency_key_invalid");
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
                "order_operations.preparation_ready.antiforgery_invalid");
        }

        var result = await service.MarkReadyAsync(
            commandId,
            parsedWorkId,
            request.Quantity,
            cancellationToken);
        return result.Outcome switch
        {
            PreparationProgressOutcome.Succeeded => Results.Ok(
                ToReadyResponse(result.Response!)),
            PreparationProgressOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            PreparationProgressOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Preparation access forbidden",
                "The current Identity is not authorized for Preparation.",
                "order_operations.preparation.forbidden"),
            PreparationProgressOutcome.WorkNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Preparation Work not found",
                "No accessible Preparation Work exists with the supplied identifier.",
                "order_operations.preparation_work.not_found"),
            PreparationProgressOutcome.QuantityInvalid => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid quantity",
                "quantity must be a positive integer.",
                "order_operations.preparation_ready.quantity_invalid"),
            PreparationProgressOutcome.AvailableQuantityInsufficient => Problem(
                StatusCodes.Status409Conflict,
                "In-Preparation quantity is insufficient",
                "The requested quantity is greater than the Work quantity currently In Preparation.",
                "order_operations.preparation_ready.in_preparation_quantity_insufficient"),
            PreparationProgressOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Preparation command.",
                "order_operations.preparation_ready.idempotency_key_conflict"),
            PreparationProgressOutcome.OrderFrozen => FrozenOrderProblem(),
            PreparationProgressOutcome.StateInconsistent => Problem(
                StatusCodes.Status500InternalServerError,
                "Preparation state is inconsistent",
                "The target Work cannot be mutated because its current State is inconsistent.",
                "order_operations.preparation.state_inconsistent"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> DeliverQuantityAsync(
        string incorporationId,
        string contentOrdinal,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        DeliverQuantityRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        DeliveryQuantityService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(incorporationId, out var parsedIncorporationId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Incorporation",
                "incorporationId must contain a UUID.",
                "order_operations.delivery.incorporation_id_invalid");
        }

        if (!int.TryParse(
                contentOrdinal,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedContentOrdinal) ||
            parsedContentOrdinal <= 0)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Content ordinal",
                "contentOrdinal must be a positive integer.",
                "order_operations.delivery.content_ordinal_invalid");
        }

        if (request.Quantity <= 0)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid quantity",
                "quantity must be a positive integer.",
                "order_operations.delivery.quantity_invalid");
        }

        if (idempotencyKey is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Delivery requires an Idempotency-Key containing a UUID v4.",
                "order_operations.delivery.idempotency_key_required");
        }

        if (!Guid.TryParse(idempotencyKey, out var commandId) ||
            !IsUuidVersion4(commandId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "order_operations.delivery.idempotency_key_invalid");
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
                "order_operations.delivery.antiforgery_invalid");
        }

        var result = await service.DeliverAsync(
            commandId,
            parsedIncorporationId,
            parsedContentOrdinal,
            request.Quantity,
            cancellationToken);
        return result.Outcome switch
        {
            DeliveryQuantityOutcome.Succeeded => Results.Ok(
                ToDeliveryResponse(result.Response!)),
            DeliveryQuantityOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            DeliveryQuantityOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Delivery access forbidden",
                "The current Identity is not authorized for Order Operations.",
                "order_operations.delivery.forbidden"),
            DeliveryQuantityOutcome.ContentNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Incorporation Content not found",
                "No Incorporation Content exists with the supplied target.",
                "order_operations.delivery.content_not_found"),
            DeliveryQuantityOutcome.QuantityInvalid => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid quantity",
                "quantity must be a positive integer.",
                "order_operations.delivery.quantity_invalid"),
            DeliveryQuantityOutcome.DeliverableQuantityInsufficient => Problem(
                StatusCodes.Status409Conflict,
                "Deliverable quantity is insufficient",
                "The requested quantity is greater than the quantity currently deliverable.",
                "order_operations.delivery.deliverable_quantity_insufficient"),
            DeliveryQuantityOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Delivery command.",
                "order_operations.delivery.idempotency_key_conflict"),
            DeliveryQuantityOutcome.StateInconsistent => Problem(
                StatusCodes.Status500InternalServerError,
                "Delivery state is inconsistent",
                "The target Content cannot be mutated because its Delivery state is inconsistent.",
                "order_operations.delivery.state_inconsistent"),
            DeliveryQuantityOutcome.OrderFrozen => FrozenOrderProblem(),
            _ => throw new UnreachableException()
        };
    }

    private static StartPreparationQuantityResponse ToStartResponse(
        PreparationCommandResult result) =>
        new(
            result.WorkId,
            result.HistoryId,
            result.OccurredAt,
            result.TotalQuantity,
            result.PendingQuantity,
            result.InPreparationQuantity,
            result.ReadyQuantity);

    private static MarkPreparationQuantityReadyResponse ToReadyResponse(
        PreparationCommandResult result) =>
        new(
            result.WorkId,
            result.HistoryId,
            result.OccurredAt,
            result.TotalQuantity,
            result.PendingQuantity,
            result.InPreparationQuantity,
            result.ReadyQuantity);

    private static DeliverQuantityResponse ToDeliveryResponse(
        DeliveryCommandResult result) =>
        new(
            result.IncorporationId,
            result.ContentOrdinal,
            result.HistoryId,
            result.OccurredAt,
            result.DeliveredQuantity);

    private static async Task<IResult> StartPendingCompositionAsync(
        string orderId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        PendingCompositionService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(orderId, out var parsedOrderId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Order",
                "orderId must contain a UUID.",
                "order.pending_composition_order_id_invalid");
        }

        if (idempotencyKey is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Starting a Pending Composition requires an Idempotency-Key containing a UUID v4.",
                "order.pending_composition_idempotency_key_required");
        }

        if (!Guid.TryParse(idempotencyKey, out var commandId) ||
            !IsUuidVersion4(commandId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "order.pending_composition_idempotency_key_invalid");
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
                "order.pending_composition_antiforgery_invalid");
        }

        var result = await service.StartAsync(commandId, parsedOrderId, cancellationToken);
        return result.Outcome switch
        {
            PendingCompositionCommandOutcome.Started => Results.Json(
                result.Response,
                statusCode: StatusCodes.Status201Created),
            PendingCompositionCommandOutcome.Unauthenticated => InvalidSession(),
            PendingCompositionCommandOutcome.Forbidden => PendingCompositionForbidden(),
            PendingCompositionCommandOutcome.OrderNotFound => PendingCompositionOrderNotFound(),
            PendingCompositionCommandOutcome.AlreadyExists => Problem(
                StatusCodes.Status409Conflict,
                "Pending Composition already exists",
                "This Order already has a current Pending Composition.",
                "order.pending_composition_already_exists"),
            PendingCompositionCommandOutcome.IdempotencyConflict => PendingCompositionIdempotencyConflict(),
            PendingCompositionCommandOutcome.OrderFrozen => FrozenOrderProblem(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> FindPendingCompositionAsync(
        string orderId,
        PendingCompositionService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(orderId, out var parsedOrderId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Order",
                "orderId must contain a UUID.",
                "order.pending_composition_order_id_invalid");
        }

        var result = await service.FindAsync(parsedOrderId, cancellationToken);
        return result.Outcome switch
        {
            CurrentPendingCompositionOutcome.Succeeded => Results.Ok(result.Response),
            CurrentPendingCompositionOutcome.Unauthenticated => InvalidSession(),
            CurrentPendingCompositionOutcome.Forbidden => PendingCompositionForbidden(),
            CurrentPendingCompositionOutcome.OrderNotFound => PendingCompositionOrderNotFound(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> DiscardPendingCompositionAsync(
        string orderId,
        string pendingCompositionId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        PendingCompositionService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(orderId, out var parsedOrderId) ||
            !Guid.TryParse(pendingCompositionId, out var parsedPendingCompositionId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Pending Composition target",
                "orderId and pendingCompositionId must contain UUIDs.",
                "order.pending_composition_target_invalid");
        }

        if (idempotencyKey is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Discarding a Pending Composition requires an Idempotency-Key containing a UUID v4.",
                "order.pending_composition_idempotency_key_required");
        }

        if (!Guid.TryParse(idempotencyKey, out var commandId) ||
            !IsUuidVersion4(commandId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "order.pending_composition_idempotency_key_invalid");
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
                "order.pending_composition_antiforgery_invalid");
        }

        var result = await service.DiscardAsync(
            commandId,
            parsedOrderId,
            parsedPendingCompositionId,
            cancellationToken);
        return result.Outcome switch
        {
            PendingCompositionCommandOutcome.Discarded => Results.NoContent(),
            PendingCompositionCommandOutcome.Unauthenticated => InvalidSession(),
            PendingCompositionCommandOutcome.Forbidden => PendingCompositionForbidden(),
            PendingCompositionCommandOutcome.OrderNotFound => PendingCompositionOrderNotFound(),
            PendingCompositionCommandOutcome.Stale => Problem(
                StatusCodes.Status409Conflict,
                "Pending Composition is stale",
                "The supplied PendingCompositionId is not the current Pending Composition for this Order.",
                "order.pending_composition_stale"),
            PendingCompositionCommandOutcome.IdempotencyConflict => PendingCompositionIdempotencyConflict(),
            PendingCompositionCommandOutcome.OrderFrozen => FrozenOrderProblem(),
            _ => throw new UnreachableException()
        };
    }

    private static IResult InvalidSession() => Problem(
        StatusCodes.Status401Unauthorized,
        "Invalid session",
        "The current session is invalid or expired.",
        "identities_and_capabilities.invalid_session");

    private static IResult PendingCompositionForbidden() => Problem(
        StatusCodes.Status403Forbidden,
        "Pending Composition forbidden",
        "The current Identity is not authorized for Order Operations.",
        "order.pending_composition_forbidden");

    private static IResult PendingCompositionOrderNotFound() => Problem(
        StatusCodes.Status404NotFound,
        "Order not found",
        "No Order exists with the supplied identifier.",
        "order_operations.order.not_found");

    private static IResult PendingCompositionIdempotencyConflict() => Problem(
        StatusCodes.Status409Conflict,
        "Idempotency-Key was already used for another intention",
        "The supplied Idempotency-Key identifies an incompatible Pending Composition command.",
        "order.pending_composition_idempotency_key_conflict");

    private static async Task<IResult> ConfirmSubsequentAsync(
        string operationalReference,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        SubsequentConfirmationRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
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
                "order_operations.subsequent_confirmation.antiforgery_invalid");
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
            SubsequentConfirmationOutcome.PendingCompositionStale => Problem(
                StatusCodes.Status409Conflict,
                "Pending Composition is stale",
                "The supplied PendingCompositionId is not the current Pending Composition for this Order.",
                "order.pending_composition_stale"),
            SubsequentConfirmationOutcome.OrderFrozen => FrozenOrderProblem(),
            SubsequentConfirmationOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            SubsequentConfirmationOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Confirmation forbidden",
                "The current Identity is not authorized for Order Operations.",
                "order_operations.confirmation.forbidden"),
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
            PreparationWorkQueryOutcome.StateInconsistent => Problem(
                StatusCodes.Status500InternalServerError,
                "Preparation state is inconsistent",
                "Preparation Work cannot be represented from its current State.",
                "order_operations.preparation.state_inconsistent"),
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

    private static async Task<IResult> FindOrderDeliveryAsync(
        string operationalReference,
        OrderDeliveryQueryService service,
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

        var result = await service.FindAsync(orderId, cancellationToken);
        return result.Outcome switch
        {
            OrderDeliveryQueryOutcome.Succeeded => Results.Ok(result.Response),
            OrderDeliveryQueryOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            OrderDeliveryQueryOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Delivery access forbidden",
                "The current Identity is not authorized for Order Operations.",
                "order_operations.delivery.forbidden"),
            OrderDeliveryQueryOutcome.OrderNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Order not found",
                "No Order exists with the supplied operational reference.",
                "order_operations.order.not_found"),
            OrderDeliveryQueryOutcome.StateInconsistent => Problem(
                StatusCodes.Status500InternalServerError,
                "Delivery state is inconsistent",
                "The Order cannot be represented by the Delivery read model.",
                "order_operations.delivery.state_inconsistent"),
            OrderDeliveryQueryOutcome.ProductReferenceInconsistent => Problem(
                StatusCodes.Status500InternalServerError,
                "Product reference is inconsistent",
                "Delivery references a Product that Catalog cannot resolve.",
                "order_operations.delivery.product_reference_inconsistent"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ConfirmFirstAsync(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        FirstConfirmationRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
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
                "order_operations.first_confirmation.antiforgery_invalid");
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
            FirstConfirmationOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            FirstConfirmationOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Confirmation forbidden",
                "The current Identity is not authorized for Order Operations.",
                "order_operations.confirmation.forbidden"),
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
