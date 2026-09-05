using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NexoBar.OrderOperations;

public static partial class OrderOperationsModule
{
    private static async Task<IResult> CloseOrderAsync(
        string orderId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ClosureService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(orderId, out var parsedOrderId))
        {
            return ClosureProblem(400, "order_id_invalid", "OrderId must contain a UUID.");
        }
        if (idempotencyKey is null)
        {
            return ClosureProblem(400, "idempotency_key_required", "Idempotency-Key is required.");
        }
        if (!Guid.TryParse(idempotencyKey, out var key) || !IsUuidVersion4(key))
        {
            return ClosureProblem(400, "idempotency_key_invalid", "Idempotency-Key must contain a UUID v4.");
        }
        if (httpContext.Request.ContentLength is > 0 || httpContext.Request.Headers.TransferEncoding.Count > 0)
        {
            return ClosureProblem(400, "body_not_allowed", "Close does not accept a request body.");
        }
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return ClosureProblem(400, "antiforgery_invalid", "A valid antiforgery token is required.");
        }

        var result = await service.CloseAsync(key, parsedOrderId, cancellationToken);
        return result.Outcome switch
        {
            ClosureOutcome.Succeeded => Results.Ok(result.Response!),
            ClosureOutcome.Unauthenticated => InvalidSession(),
            ClosureOutcome.Forbidden => ClosureProblem(403, "forbidden", "OrderOperationsAndBasicClosure is required."),
            ClosureOutcome.OrderNotFound => ClosureProblem(404, "order_not_found", "The Order does not exist."),
            ClosureOutcome.NotLiquidated => ClosureProblem(409, "not_liquidated", "The Order must be Liquidated before Closure."),
            ClosureOutcome.AlreadyClosed => ClosureProblem(409, "already_closed", "The Order is already Closed."),
            ClosureOutcome.IdempotencyConflict => ClosureProblem(409, "idempotency_key_conflict", "The key identifies an incompatible Closure command."),
            ClosureOutcome.StateInconsistent => ClosureProblem(409, "state_inconsistent", "The Order has structurally contradictory terminal State."),
            _ => throw new InvalidOperationException("Unknown Closure outcome.")
        };
    }

    private static IResult ClosureProblem(int status, string code, string detail) =>
        Problem(status, "Order Closure rejected", detail, $"order_operations.closure.{code}");
}
