using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NexoBar.OrderOperations;

public static partial class OrderOperationsModule
{
    private static async Task<IResult> CancelContentAsync(
        string orderId, string incorporationId, string contentOrdinal,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext httpContext, IAntiforgery antiforgery, ContentCancellationService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(orderId, out var order) || order == Guid.Empty ||
            !Guid.TryParse(incorporationId, out var incorporation) || incorporation == Guid.Empty ||
            !int.TryParse(contentOrdinal, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) || ordinal <= 0)
            return ContentCancellationProblem(400, "target_invalid", "A valid Order, Incorporation and positive content ordinal are required.");
        if (idempotencyKey is null)
            return ContentCancellationProblem(400, "idempotency_key_required", "Idempotency-Key is required.");
        if (!Guid.TryParse(idempotencyKey, out var key) || !IsUuidVersion4(key))
            return ContentCancellationProblem(400, "idempotency_key_invalid", "Idempotency-Key must contain a UUID v4.");
        try { await antiforgery.ValidateRequestAsync(httpContext); }
        catch (AntiforgeryValidationException)
        {
            return ContentCancellationProblem(400, "antiforgery_invalid", "A valid antiforgery token is required.");
        }
        if (!httpContext.Request.HasJsonContentType())
            return ContentCancellationProblem(400, "request_invalid", "A JSON cancellation request is required.");
        CancelContentRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<CancelContentRequest>(cancellationToken);
        }
        catch (JsonException)
        {
            return ContentCancellationProblem(400, "request_invalid", "The cancellation request is malformed or contains unsupported properties.");
        }
        if (request is null)
            return ContentCancellationProblem(400, "request_invalid", "A cancellation request is required.");
        if (request.Quantity <= 0)
            return ContentCancellationProblem(400, "quantity_invalid", "quantity must be a positive integer.");
        var result = await service.CancelAsync(key, order, incorporation, ordinal, request.Quantity, cancellationToken);
        return result.Outcome switch
        {
            ContentCancellationOutcome.Succeeded => Results.Ok(result.Response!),
            ContentCancellationOutcome.Unauthenticated => InvalidSession(),
            ContentCancellationOutcome.Forbidden => ContentCancellationProblem(403, "forbidden", "OrderOperationsAndBasicClosure is required."),
            ContentCancellationOutcome.ContentNotFound => ContentCancellationProblem(404, "content_not_found", "The confirmed-content target does not exist in this Order."),
            ContentCancellationOutcome.QuantityInvalid => ContentCancellationProblem(400, "quantity_invalid", "quantity must be a positive integer."),
            ContentCancellationOutcome.QuantityExceedsEligible => ContentCancellationProblem(409, "quantity_exceeds_eligible", "The exact cancellation exceeds current eligible content quantity."),
            ContentCancellationOutcome.OrderFrozen => FrozenOrderProblem(),
            ContentCancellationOutcome.IdempotencyConflict => ContentCancellationProblem(409, "idempotency_key_conflict", "The key identifies an incompatible Content Cancellation intent."),
            ContentCancellationOutcome.StateInconsistent => ContentCancellationProblem(500, "state_inconsistent", "The current fulfillment State is inconsistent."),
            _ => throw new InvalidOperationException("Unknown Content Cancellation outcome.")
        };
    }

    private static IResult ContentCancellationProblem(int status, string code, string detail) =>
        Problem(status, "Content Cancellation rejected", detail, $"order_operations.content_cancellation.{code}");
}
