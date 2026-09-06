using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NexoBar.OrderOperations;

public static partial class OrderOperationsModule
{
    private static async Task<IResult> CorrectContentAsync(
        string orderId, string incorporationId, string contentOrdinal,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext httpContext, IAntiforgery antiforgery, ContentCorrectionService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(orderId, out var order) || order == Guid.Empty ||
            !Guid.TryParse(incorporationId, out var incorporation) || incorporation == Guid.Empty ||
            !int.TryParse(contentOrdinal, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) || ordinal <= 0)
            return ContentCorrectionProblem(400, "target_invalid", "A valid Order, Incorporation and positive content ordinal are required.");
        if (idempotencyKey is null)
            return ContentCorrectionProblem(400, "idempotency_key_required", "Idempotency-Key is required.");
        if (!Guid.TryParse(idempotencyKey, out var key) || !IsUuidVersion4(key))
            return ContentCorrectionProblem(400, "idempotency_key_invalid", "Idempotency-Key must contain a UUID v4.");
        try { await antiforgery.ValidateRequestAsync(httpContext); }
        catch (AntiforgeryValidationException)
        {
            return ContentCorrectionProblem(400, "antiforgery_invalid", "A valid antiforgery token is required.");
        }
        if (!httpContext.Request.HasJsonContentType())
            return ContentCorrectionProblem(400, "request_invalid", "A JSON correction request is required.");
        CorrectContentRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<CorrectContentRequest>(cancellationToken);
        }
        catch (JsonException)
        {
            return ContentCorrectionProblem(400, "request_invalid", "The correction request is malformed or contains unsupported properties.");
        }
        if (request is null)
            return ContentCorrectionProblem(400, "request_invalid", "A correction request is required.");
        if (request.Quantity <= 0)
            return ContentCorrectionProblem(400, "quantity_invalid", "quantity must be a positive integer.");
        var result = await service.CorrectAsync(key, order, incorporation, ordinal, request.Quantity, cancellationToken);
        return result.Outcome switch
        {
            ContentCorrectionOutcome.Succeeded => Results.Ok(result.Response!),
            ContentCorrectionOutcome.Unauthenticated => InvalidSession(),
            ContentCorrectionOutcome.Forbidden => ContentCorrectionProblem(403, "forbidden", "OrderOperationsAndBasicClosure is required."),
            ContentCorrectionOutcome.ContentNotFound => ContentCorrectionProblem(404, "content_not_found", "The confirmed-content target does not exist in this Order."),
            ContentCorrectionOutcome.QuantityInvalid => ContentCorrectionProblem(400, "quantity_invalid", "quantity must be a positive integer."),
            ContentCorrectionOutcome.QuantityExceedsEligible => ContentCorrectionProblem(409, "quantity_exceeds_eligible", "The exact correction exceeds current eligible content quantity."),
            ContentCorrectionOutcome.OrderFrozen => FrozenOrderProblem(),
            ContentCorrectionOutcome.IdempotencyConflict => ContentCorrectionProblem(409, "idempotency_key_conflict", "The key identifies an incompatible Content Correction intent."),
            ContentCorrectionOutcome.StateInconsistent => ContentCorrectionProblem(500, "state_inconsistent", "The current fulfillment State is inconsistent."),
            _ => throw new InvalidOperationException("Unknown Content Correction outcome.")
        };
    }

    private static IResult ContentCorrectionProblem(int status, string code, string detail) =>
        Problem(status, "Content Correction rejected", detail, $"order_operations.content_correction.{code}");
}
