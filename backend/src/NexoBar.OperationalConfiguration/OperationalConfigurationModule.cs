using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.OperationalConfiguration;

public static class OperationalConfigurationModule
{
    public static IServiceCollection AddOperationalConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(
            "OperationalConfiguration")
            ?? throw new InvalidOperationException(
                "Connection string 'OperationalConfiguration' is required.");

        services.AddDbContext<OperationalConfigurationDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "operational_configuration")));
        services.AddScoped<PreparationResponsibilityService>();
        services.AddScoped<IPreparationResponsibilityLookup,
            PreparationResponsibilityLookup>();
        return services;
    }

    public static IEndpointRouteBuilder MapOperationalConfigurationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(
                "/api/operational-configuration/preparation-responsibilities")
            .WithTags("OperationalConfiguration");
        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreatePreparationResponsibility")
            .Accepts<CreatePreparationResponsibilityRequest>("application/json")
            .Produces<PreparationResponsibilityResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet(string.Empty, ListAsync)
            .WithName("ListPreparationResponsibilities")
            .Produces<IReadOnlyList<PreparationResponsibilityResponse>>();
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        CreatePreparationResponsibilityRequest request,
        PreparationResponsibilityService service,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Preparation Responsibility creation requires an Idempotency-Key containing a UUID v4.",
                "operational_configuration.preparation_responsibility.idempotency_key_required");
        }
        if (!Guid.TryParse(idempotencyKey, out var commandId) ||
            !IsUuidVersion4(commandId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "operational_configuration.preparation_responsibility.idempotency_key_invalid");
        }

        var result = await service.CreateAsync(commandId, request, cancellationToken);
        return result.Outcome switch
        {
            CreatePreparationResponsibilityOutcome.Created => Results.Created(
                $"/api/operational-configuration/preparation-responsibilities/{result.Responsibility!.Id}",
                result.Responsibility),
            CreatePreparationResponsibilityOutcome.Invalid => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Preparation Responsibility",
                "Operational name must contain non-whitespace text.",
                "operational_configuration.preparation_responsibility.operational_name_invalid"),
            CreatePreparationResponsibilityOutcome.DuplicateName => Problem(
                StatusCodes.Status409Conflict,
                "Operational name already in use",
                "A Preparation Responsibility already uses that operational name, ignoring case.",
                "operational_configuration.preparation_responsibility.operational_name_conflict"),
            CreatePreparationResponsibilityOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies another Preparation Responsibility creation.",
                "operational_configuration.preparation_responsibility.idempotency_key_conflict"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ListAsync(
        PreparationResponsibilityService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(cancellationToken));

    private static IResult Problem(
        int statusCode,
        string title,
        string detail,
        string code) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static bool IsUuidVersion4(Guid value)
    {
        var bytes = value.ToByteArray(bigEndian: true);
        return bytes[6] >> 4 == 4 && (bytes[8] & 0xc0) == 0x80;
    }
}
