using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NexoBar.OrderOperations;

public static partial class OrderOperationsModule
{
    private static async Task<IResult> CorrectDeliveryAsync(
        string orderId, string incorporationId, string contentOrdinal,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext httpContext, IAntiforgery antiforgery, DeliveryCorrectionService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(orderId, out var order) || order == Guid.Empty ||
            !Guid.TryParse(incorporationId, out var incorporation) || incorporation == Guid.Empty ||
            !int.TryParse(contentOrdinal, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) || ordinal <= 0)
            return DeliveryCorrectionProblem(400, "target_invalid", "A valid Order, Incorporation and positive content ordinal are required.");
        if (idempotencyKey is null)
            return DeliveryCorrectionProblem(400, "idempotency_key_required", "Idempotency-Key is required.");
        if (!Guid.TryParse(idempotencyKey, out var key) || !IsUuidVersion4(key))
            return DeliveryCorrectionProblem(400, "idempotency_key_invalid", "Idempotency-Key must contain a UUID v4.");
        try { await antiforgery.ValidateRequestAsync(httpContext); }
        catch (AntiforgeryValidationException)
        {
            return DeliveryCorrectionProblem(400, "antiforgery_invalid", "A valid antiforgery token is required.");
        }
        if (!httpContext.Request.HasJsonContentType())
            return DeliveryCorrectionProblem(400, "request_invalid", "A JSON correction request is required.");
        CorrectDeliveryRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<CorrectDeliveryRequest>(cancellationToken);
        }
        catch (JsonException)
        {
            return DeliveryCorrectionProblem(400, "request_invalid", "The correction request is malformed or contains unsupported properties.");
        }
        if (request is null)
            return DeliveryCorrectionProblem(400, "request_invalid", "A correction request is required.");
        if (request.Quantity <= 0)
            return DeliveryCorrectionProblem(400, "quantity_invalid", "quantity must be a positive integer.");
        var result = await service.CorrectAsync(key, order, incorporation, ordinal, request.Quantity, cancellationToken);
        return result.Outcome switch
        {
            DeliveryCorrectionOutcome.Succeeded => Results.Ok(result.Response!),
            DeliveryCorrectionOutcome.Unauthenticated => InvalidSession(),
            DeliveryCorrectionOutcome.Forbidden => DeliveryCorrectionProblem(403, "forbidden", "OrderOperationsAndBasicClosure is required."),
            DeliveryCorrectionOutcome.ContentNotFound => DeliveryCorrectionProblem(404, "content_not_found", "The confirmed-content target does not exist in this Order."),
            DeliveryCorrectionOutcome.QuantityInvalid => DeliveryCorrectionProblem(400, "quantity_invalid", "quantity must be a positive integer."),
            DeliveryCorrectionOutcome.NoEffectiveDelivery => DeliveryCorrectionProblem(409, "no_effective_delivery", "There is no effective delivered quantity to correct."),
            DeliveryCorrectionOutcome.QuantityExceedsDelivered => DeliveryCorrectionProblem(409, "quantity_exceeds_delivered", "The exact correction exceeds current effective delivered quantity."),
            DeliveryCorrectionOutcome.OrderFrozen => FrozenOrderProblem(),
            DeliveryCorrectionOutcome.IdempotencyConflict => DeliveryCorrectionProblem(409, "idempotency_key_conflict", "The key identifies an incompatible Delivery Correction intent."),
            DeliveryCorrectionOutcome.StateInconsistent => DeliveryCorrectionProblem(500, "state_inconsistent", "The current fulfillment State is inconsistent."),
            _ => throw new InvalidOperationException("Unknown Delivery Correction outcome.")
        };
    }

    private static IResult DeliveryCorrectionProblem(int status, string code, string detail) =>
        Problem(status, "Delivery Correction rejected", detail, $"order_operations.delivery_correction.{code}");
}
