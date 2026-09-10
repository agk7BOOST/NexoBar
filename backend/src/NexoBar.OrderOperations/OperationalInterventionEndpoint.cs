using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace NexoBar.OrderOperations;

public static partial class OrderOperationsModule
{
    private static void MapOperationalInterventionEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/order-operations/intervention/work/{workId}/in-preparation", InterveneInPreparationAsync)
            .WithName("InterveneInPreparationQuantity").WithTags("OrderOperations").RequireAuthorization()
            .Accepts<OperationalInterventionRequest>("application/json").Produces<OperationalInterventionResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(500);
        endpoints.MapPost("/api/order-operations/intervention/work/{workId}/ready", InterveneReadyAsync)
            .WithName("InterveneReadyQuantity").WithTags("OrderOperations").RequireAuthorization()
            .Accepts<OperationalInterventionRequest>("application/json").Produces<OperationalInterventionResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(500);
        endpoints.MapGet("/api/order-operations/intervention/incorporations/{incorporationId}/contents/{contentOrdinal}", FindInterventionTargetAsync)
            .WithName("GetOperationalInterventionTarget").WithTags("OrderOperations").RequireAuthorization()
            .Produces<OperationalInterventionTargetResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(500);
    }

    private static Task<IResult> InterveneInPreparationAsync(string workId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext httpContext, IAntiforgery antiforgery, OperationalInterventionService service, CancellationToken cancellationToken) =>
        InterveneAsync(false, workId, idempotencyKey, httpContext, antiforgery, service, cancellationToken);

    private static Task<IResult> InterveneReadyAsync(string workId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext httpContext, IAntiforgery antiforgery, OperationalInterventionService service, CancellationToken cancellationToken) =>
        InterveneAsync(true, workId, idempotencyKey, httpContext, antiforgery, service, cancellationToken);

    private static async Task<IResult> InterveneAsync(bool ready, string workId, string? idempotencyKey,
        HttpContext context, IAntiforgery antiforgery, OperationalInterventionService service, CancellationToken token)
    {
        if (!Guid.TryParse(workId, out var work) || work == Guid.Empty)
            return InterventionProblem(400, "target_invalid", "An exact Work UUID is required.");
        if (idempotencyKey is null)
            return InterventionProblem(400, "idempotency_key_required", "Idempotency-Key is required.");
        if (!Guid.TryParse(idempotencyKey, out var key) || !IsUuidVersion4(key))
            return InterventionProblem(400, "idempotency_key_invalid", "Idempotency-Key must contain a UUID v4.");
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException)
        {
            return InterventionProblem(400, "antiforgery_invalid", "A valid antiforgery token is required.");
        }
        if (!context.Request.HasJsonContentType())
            return InterventionProblem(400, "request_invalid", "A JSON intervention request is required.");
        OperationalInterventionRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<OperationalInterventionRequest>(token); }
        catch (JsonException)
        {
            return InterventionProblem(400, "request_invalid", "The intervention request is malformed or contains unsupported properties.");
        }
        if (request is null) return InterventionProblem(400, "request_invalid", "An intervention request is required.");
        if (request.Quantity <= 0) return InterventionProblem(400, "quantity_invalid", "quantity must be a positive integer.");
        var result = ready
            ? await service.InterveneReadyAsync(key, work, request.Quantity, token)
            : await service.InterveneInPreparationAsync(key, work, request.Quantity, token);
        if (result.Response is { } response)
            return Results.Ok(new OperationalInterventionResponse(response.WorkId, response.HistoryId, response.OccurredAt,
                response.TotalQuantity, response.PendingQuantity, response.InPreparationQuantity, response.ReadyQuantity));
        return InterventionFailure(result.Outcome);
    }

    private static async Task<IResult> FindInterventionTargetAsync(string incorporationId, string contentOrdinal,
        OperationalInterventionQueryService service, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(incorporationId, out var incorporation) || incorporation == Guid.Empty ||
            !int.TryParse(contentOrdinal, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) || ordinal <= 0)
            return InterventionProblem(400, "target_invalid", "An exact Incorporation UUID and positive Content ordinal are required.");
        var result = await service.FindAsync(incorporation, ordinal, cancellationToken);
        return result.Response is { } response ? Results.Ok(response) : InterventionFailure(result.Outcome);
    }

    private static IResult InterventionFailure(PreparationProgressOutcome outcome) => outcome switch
    {
        PreparationProgressOutcome.Unauthenticated => InvalidSession(),
        PreparationProgressOutcome.Forbidden => InterventionProblem(403, "forbidden", "OperationalIntervention is required."),
        PreparationProgressOutcome.WorkNotFound => InterventionProblem(404, "target_not_found", "The intervention target does not exist."),
        PreparationProgressOutcome.QuantityInvalid => InterventionProblem(400, "quantity_invalid", "quantity must be a positive integer."),
        PreparationProgressOutcome.AvailableQuantityInsufficient => InterventionProblem(409, "quantity_exceeds_eligible", "The exact intervention exceeds the current eligible source-stage quantity."),
        PreparationProgressOutcome.IdempotencyConflict => InterventionProblem(409, "idempotency_key_conflict", "The key identifies an incompatible Work command intent."),
        PreparationProgressOutcome.OrderFrozen => FrozenOrderProblem(),
        PreparationProgressOutcome.StateInconsistent => InterventionProblem(500, "state_inconsistent", "The current fulfillment State is inconsistent."),
        _ => throw new InvalidOperationException("Unknown OperationalIntervention outcome.")
    };

    private static IResult InterventionProblem(int status, string code, string detail) =>
        Problem(status, "OperationalIntervention rejected", detail, $"order_operations.intervention.{code}");
}
