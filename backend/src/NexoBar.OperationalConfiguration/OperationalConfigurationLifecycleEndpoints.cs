using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace NexoBar.OperationalConfiguration;

public static partial class OperationalConfigurationModule
{
    private static void MapContextLifecycle(RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/operational-name-changes", RenameContextAsync).WithName("RenameOperationalContext")
            .Accepts<RenameConfigurationRequest>("application/json").Produces<ConfigurationLifecycleResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/{id:guid}/retire", RetireContextAsync).WithName("RetireOperationalContext")
            .Accepts<ConfigurationLifecycleRequest>("application/json").Produces<ConfigurationLifecycleResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/{id:guid}/reactivate", ReactivateContextAsync).WithName("ReactivateOperationalContext")
            .Accepts<ConfigurationLifecycleRequest>("application/json").Produces<ConfigurationLifecycleResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        group.MapDelete("/{id:guid}", DeleteContextAsync).WithName("DeleteOperationalContext")
            .Accepts<ConfigurationLifecycleRequest>("application/json").Produces<ConfigurationLifecycleResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
    }

    private static async Task<IResult> RenameContextAsync(
        Guid id, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        [FromBody] RenameConfigurationRequest request, HttpContext context, IAntiforgery antiforgery,
        OperationalContextLifecycleService service, CancellationToken token)
    {
        var validation = await ValidateLifecycleAsync(idempotencyKey, context, antiforgery, "context");
        if (validation.Failure is not null) return validation.Failure;
        return LifecycleResult(await service.ExecuteAsync(validation.Key!.Value, id, "Rename", new(request.ExpectedCurrentOperationalName, request.ExpectedIsActive), request.NewOperationalName, token), "context");
    }
    private static async Task<IResult> RetireContextAsync(
        Guid id, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        [FromBody] ConfigurationLifecycleRequest request, HttpContext context, IAntiforgery antiforgery,
        OperationalContextLifecycleService service, CancellationToken token)
    {
        var validation = await ValidateLifecycleAsync(idempotencyKey, context, antiforgery, "context");
        if (validation.Failure is not null) return validation.Failure;
        return LifecycleResult(await service.ExecuteAsync(validation.Key!.Value, id, "Retire", request, null, token), "context");
    }
    private static async Task<IResult> ReactivateContextAsync(
        Guid id, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        [FromBody] ConfigurationLifecycleRequest request, HttpContext context, IAntiforgery antiforgery,
        OperationalContextLifecycleService service, CancellationToken token)
    {
        var validation = await ValidateLifecycleAsync(idempotencyKey, context, antiforgery, "context");
        if (validation.Failure is not null) return validation.Failure;
        return LifecycleResult(await service.ExecuteAsync(validation.Key!.Value, id, "Reactivate", request, null, token), "context");
    }
    private static async Task<IResult> DeleteContextAsync(
        Guid id, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        [FromBody] ConfigurationLifecycleRequest request, HttpContext context, IAntiforgery antiforgery,
        OperationalContextLifecycleService service, CancellationToken token)
    {
        var validation = await ValidateLifecycleAsync(idempotencyKey, context, antiforgery, "context");
        if (validation.Failure is not null) return validation.Failure;
        return LifecycleResult(await service.ExecuteAsync(validation.Key!.Value, id, "Delete", request, null, token), "context");
    }
    private static void MapDestinationLifecycle(RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/operational-name-changes", RenameDestinationAsync).WithName("RenamePreparationResponsibility")
            .Accepts<RenameConfigurationRequest>("application/json").Produces<ConfigurationLifecycleResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/{id:guid}/retire", RetireDestinationAsync).WithName("RetirePreparationResponsibility")
            .Accepts<ConfigurationLifecycleRequest>("application/json").Produces<ConfigurationLifecycleResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/{id:guid}/reactivate", ReactivateDestinationAsync).WithName("ReactivatePreparationResponsibility")
            .Accepts<ConfigurationLifecycleRequest>("application/json").Produces<ConfigurationLifecycleResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        group.MapDelete("/{id:guid}", DeleteDestinationAsync).WithName("DeletePreparationResponsibility")
            .Accepts<ConfigurationLifecycleRequest>("application/json").Produces<ConfigurationLifecycleResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
    }

    private static async Task<IResult> RenameDestinationAsync(
        Guid id, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        [FromBody] RenameConfigurationRequest request, HttpContext context, IAntiforgery antiforgery,
        PreparationResponsibilityLifecycleService service, CancellationToken token)
    {
        var validation = await ValidateLifecycleAsync(idempotencyKey, context, antiforgery, "preparation_responsibility");
        if (validation.Failure is not null) return validation.Failure;
        return LifecycleResult(await service.ExecuteAsync(validation.Key!.Value, id, "Rename", new(request.ExpectedCurrentOperationalName, request.ExpectedIsActive), request.NewOperationalName, token), "preparation_responsibility");
    }
    private static async Task<IResult> RetireDestinationAsync(
        Guid id, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        [FromBody] ConfigurationLifecycleRequest request, HttpContext context, IAntiforgery antiforgery,
        PreparationResponsibilityLifecycleService service, CancellationToken token)
    {
        var validation = await ValidateLifecycleAsync(idempotencyKey, context, antiforgery, "preparation_responsibility");
        if (validation.Failure is not null) return validation.Failure;
        return LifecycleResult(await service.ExecuteAsync(validation.Key!.Value, id, "Retire", request, null, token), "preparation_responsibility");
    }
    private static async Task<IResult> ReactivateDestinationAsync(
        Guid id, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        [FromBody] ConfigurationLifecycleRequest request, HttpContext context, IAntiforgery antiforgery,
        PreparationResponsibilityLifecycleService service, CancellationToken token)
    {
        var validation = await ValidateLifecycleAsync(idempotencyKey, context, antiforgery, "preparation_responsibility");
        if (validation.Failure is not null) return validation.Failure;
        return LifecycleResult(await service.ExecuteAsync(validation.Key!.Value, id, "Reactivate", request, null, token), "preparation_responsibility");
    }
    private static async Task<IResult> DeleteDestinationAsync(
        Guid id, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        [FromBody] ConfigurationLifecycleRequest request, HttpContext context, IAntiforgery antiforgery,
        PreparationResponsibilityLifecycleService service, CancellationToken token)
    {
        var validation = await ValidateLifecycleAsync(idempotencyKey, context, antiforgery, "preparation_responsibility");
        if (validation.Failure is not null) return validation.Failure;
        return LifecycleResult(await service.ExecuteAsync(validation.Key!.Value, id, "Delete", request, null, token), "preparation_responsibility");
    }
    private static async Task<(Guid? Key, IResult? Failure)> ValidateLifecycleAsync(
        string? value, HttpContext context, IAntiforgery antiforgery, string entity)
    {
        if (value is null) return (null, Problem(400, "Idempotency-Key is required", "A UUID v4 is required.", $"operational_configuration.{entity}.idempotency_key_required"));
        if (!Guid.TryParse(value, out var key) || !IsUuidVersion4(key))
            return (null, Problem(400, "Invalid Idempotency-Key", "A UUID v4 is required.", $"operational_configuration.{entity}.idempotency_key_invalid"));
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException)
        { return (null, Problem(400, "Antiforgery validation failed", "A valid antiforgery token is required.", "identities_and_capabilities.antiforgery_invalid")); }
        return (key, null);
    }

    private static IResult LifecycleResult(ConfigurationMutationResult result, string entity) => result.Status switch
    {
        200 => Results.Ok(result.Result),
        401 => AuthenticationRequired(),
        403 => GeneralConfigurationRequired(),
        _ => Problem(result.Status, "Configuration command rejected", result.Detail!, $"operational_configuration.{entity}.{result.Code}")
    };
}
