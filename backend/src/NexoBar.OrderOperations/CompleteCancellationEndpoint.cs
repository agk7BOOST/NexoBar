using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace NexoBar.OrderOperations;

public static partial class OrderOperationsModule
{
    private static void MapCompleteCancellationEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/orders/{orderId}/complete-cancellation", CancelOrderCompletelyAsync)
            .WithName("CompleteOrderCancellation").WithTags("OrderOperations").RequireAuthorization()
            .Produces<CompleteCancellationResponse>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(500);
        endpoints.MapGet("/api/orders/{orderId}/complete-cancellation", EvaluateCompleteCancellationAsync)
            .WithName("EvaluateCompleteOrderCancellation").WithTags("OrderOperations").RequireAuthorization()
            .Produces<CompleteCancellationEvaluation>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(404);
    }

    private static async Task<IResult> CancelOrderCompletelyAsync(string orderId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, HttpContext context,
        IAntiforgery antiforgery, CompleteCancellationService service, CancellationToken token)
    {
        if (!Guid.TryParse(orderId, out var id)) return CompleteCancellationProblem(400, "order_id_invalid");
        if (idempotencyKey is null) return CompleteCancellationProblem(400, "idempotency_key_required");
        if (!Guid.TryParse(idempotencyKey, out var key) || !IsUuidVersion4(key))
            return CompleteCancellationProblem(400, "idempotency_key_invalid");
        if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
            return CompleteCancellationProblem(400, "body_not_allowed");
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { return CompleteCancellationProblem(400, "antiforgery_invalid"); }
        return CompleteCancellationHttpResult(await service.ExecuteAsync(id, key, token));
    }

    private static async Task<IResult> EvaluateCompleteCancellationAsync(string orderId, CompleteCancellationService service, CancellationToken token)
    {
        if (!Guid.TryParse(orderId, out var id)) return CompleteCancellationProblem(400, "order_id_invalid");
        return CompleteCancellationHttpResult(await service.ExecuteAsync(id, null, token));
    }

    private static IResult CompleteCancellationHttpResult(CompleteCancellationResult result) => result.Error switch
    {
        null => result.Response is not null ? Results.Ok(result.Response) : Results.Ok(result.Evaluation!),
        "unauthenticated" => InvalidSession(),
        "forbidden" or "operational_intervention_required" => CompleteCancellationProblem(403, result.Error),
        "order_not_found" => CompleteCancellationProblem(404, result.Error),
        "state_inconsistent" => CompleteCancellationProblem(500, result.Error),
        _ => CompleteCancellationProblem(409, result.Error)
    };

    private static IResult CompleteCancellationProblem(int status, string code) =>
        Problem(status, "Complete Order Cancellation rejected", code, $"order_operations.complete_cancellation.{code}");

    private static IResult CancelledOrderProblem() => Problem(409, "Order is terminal",
        "The Order terminated through Complete Cancellation.", "order_operations.order_completely_cancelled");
}
