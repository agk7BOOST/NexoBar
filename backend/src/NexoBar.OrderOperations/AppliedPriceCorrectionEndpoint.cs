using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace NexoBar.OrderOperations;

public static partial class OrderOperationsModule
{
    private static void MapAppliedPriceCorrectionEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/order-operations/orders/{orderId}/incorporations/{incorporationId}/contents/{contentOrdinal}/applied-price-correction", GetAppliedPriceCorrectionEvaluationAsync)
            .WithName("GetAppliedPriceCorrectionEvaluation").WithTags("OrderOperations").RequireAuthorization()
            .Produces<AppliedPriceCorrectionEvaluationResponse>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(500);
        endpoints.MapPost("/api/order-operations/orders/{orderId}/incorporations/{incorporationId}/contents/{contentOrdinal}/apply-current-catalog-price", ApplyCurrentCatalogPriceAsync)
            .WithName("ApplyCurrentCatalogPrice")
            .WithTags("OrderOperations")
            .RequireAuthorization()
            .Accepts<ApplyCurrentCatalogPriceRequest>("application/json")
            .Produces<AppliedPriceCorrectionResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(500);
    }

    private static async Task<IResult> GetAppliedPriceCorrectionEvaluationAsync(Guid orderId, Guid incorporationId, int contentOrdinal, AppliedPriceCorrectionService service, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty || incorporationId == Guid.Empty || contentOrdinal <= 0) return Problem(400, "Applied Price Correction rejected", "A valid Order, Incorporation and positive content ordinal are required.", "order_operations.applied_price_correction.target_invalid");
        var result = await service.EvaluateAsync(orderId, incorporationId, contentOrdinal, cancellationToken);
        return result.Outcome switch
        {
            AppliedPriceCorrectionEvaluationOutcome.Succeeded => Results.Ok(result.Response!),
            AppliedPriceCorrectionEvaluationOutcome.Unauthenticated => InvalidSession(),
            AppliedPriceCorrectionEvaluationOutcome.Forbidden => Problem(403, "Applied Price Correction rejected", "OrderOperationsAndBasicClosure is required.", "order_operations.applied_price_correction.forbidden"),
            AppliedPriceCorrectionEvaluationOutcome.ContentNotFound => Problem(404, "Applied Price Correction rejected", "The confirmed-content target does not exist in this Order.", "order_operations.applied_price_correction.content_not_found"),
            AppliedPriceCorrectionEvaluationOutcome.StateInconsistent => Problem(500, "Applied Price Correction rejected", "The current price State is inconsistent.", "order_operations.applied_price_correction.state_inconsistent"),
            _ => throw new InvalidOperationException("Unknown Applied Price Correction evaluation outcome.")
        };
    }

    private static async Task<IResult> ApplyCurrentCatalogPriceAsync(
        Guid orderId, Guid incorporationId, int contentOrdinal,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext httpContext, IAntiforgery antiforgery, AppliedPriceCorrectionService service,
        CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty || incorporationId == Guid.Empty || contentOrdinal <= 0)
            return Problem(400, "Applied Price Correction rejected", "A valid Order, Incorporation and positive content ordinal are required.", "order_operations.applied_price_correction.target_invalid");
        if (idempotencyKey is null) return Problem(400, "Applied Price Correction rejected", "Idempotency-Key is required.", "order_operations.applied_price_correction.idempotency_key_required");
        if (!Guid.TryParse(idempotencyKey, out var key) || !IsUuidVersion4(key))
            return Problem(400, "Applied Price Correction rejected", "Idempotency-Key must contain a UUID v4.", "order_operations.applied_price_correction.idempotency_key_invalid");
        try { await antiforgery.ValidateRequestAsync(httpContext); }
        catch (AntiforgeryValidationException) { return Problem(400, "Applied Price Correction rejected", "A valid antiforgery token is required.", "order_operations.applied_price_correction.antiforgery_invalid"); }
        ApplyCurrentCatalogPriceRequest? request;
        try { request = await httpContext.Request.ReadFromJsonAsync<ApplyCurrentCatalogPriceRequest>(cancellationToken); }
        catch (JsonException) { return Problem(400, "Applied Price Correction rejected", "The correction request is malformed or contains unsupported properties.", "order_operations.applied_price_correction.request_invalid"); }
        if (request is null) return Problem(400, "Applied Price Correction rejected", "An empty correction request is required.", "order_operations.applied_price_correction.request_invalid");
        var result = await service.CorrectAsync(key, orderId, incorporationId, contentOrdinal, cancellationToken);
        return result.Outcome switch
        {
            AppliedPriceCorrectionOutcome.Succeeded => Results.Ok(result.Response!),
            AppliedPriceCorrectionOutcome.Unauthenticated => InvalidSession(),
            AppliedPriceCorrectionOutcome.Forbidden => Problem(403, "Applied Price Correction rejected", "OrderOperationsAndBasicClosure is required.", "order_operations.applied_price_correction.forbidden"),
            AppliedPriceCorrectionOutcome.ContentNotFound => Problem(404, "Applied Price Correction rejected", "The confirmed-content target does not exist in this Order.", "order_operations.applied_price_correction.content_not_found"),
            AppliedPriceCorrectionOutcome.ProductNotCurrent => Problem(409, "Applied Price Correction rejected", "The Content Product has no current valid Catalog price.", "order_operations.applied_price_correction.product_not_current"),
            AppliedPriceCorrectionOutcome.NoCorrectionToApply => Problem(409, "Applied Price Correction rejected", "The current Catalog price already equals EffectiveAppliedPrice.", "order_operations.applied_price_correction.no_correction_to_apply"),
            AppliedPriceCorrectionOutcome.OrderCancelled => CancelledOrderProblem(),
            AppliedPriceCorrectionOutcome.OrderFrozen => FrozenOrderProblem(),
            AppliedPriceCorrectionOutcome.OrderClosed => Problem(409, "Applied Price Correction rejected", "The Order is closed.", "order_operations.applied_price_correction.order_closed"),
            AppliedPriceCorrectionOutcome.IdempotencyConflict => Problem(409, "Applied Price Correction rejected", "The key identifies an incompatible Applied Price Correction intent.", "order_operations.applied_price_correction.idempotency_key_conflict"),
            AppliedPriceCorrectionOutcome.StateInconsistent => Problem(500, "Applied Price Correction rejected", "The current price State is inconsistent.", "order_operations.applied_price_correction.state_inconsistent"),
            _ => throw new InvalidOperationException("Unknown Applied Price Correction outcome.")
        };
    }
}
