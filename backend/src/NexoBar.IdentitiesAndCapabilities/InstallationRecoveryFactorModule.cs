using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace NexoBar.IdentitiesAndCapabilities;

public static class InstallationRecoveryFactorModule
{
    public static IEndpointRouteBuilder MapInstallationRecoveryFactorEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/installation-recovery-factor/rotate",
                RotateAsync)
            .RequireAuthorization()
            .WithTags("Installation Recovery Factor")
            .WithName("RotateInstallationRecoveryFactor")
            .Accepts<InstallationRecoveryFactorRequest>("application/json")
            .Produces<InstallationRecoveryFactorResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return endpoints;
    }

    private static async Task<IResult> RotateAsync(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        InstallationRecoveryFactorRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        InstallationRecoveryFactorService service,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateCommandAsync(
            idempotencyKey,
            httpContext,
            antiforgery);
        if (validation.Error is not null)
        {
            return validation.Error;
        }

        return MapResult(await service.RotateAsync(
            validation.Key,
            request,
            cancellationToken));
    }

    private static async Task<CommandValidation> ValidateCommandAsync(
        string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery)
    {
        if (idempotencyKey is null)
        {
            return CommandValidation.Failed(Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Administrative commands require an Idempotency-Key containing a UUID v4.",
                "identities_and_capabilities.idempotency_key_required"));
        }

        if (!Guid.TryParse(idempotencyKey, out var key) || !IsUuidVersion4(key))
        {
            return CommandValidation.Failed(Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "identities_and_capabilities.idempotency_key_invalid"));
        }

        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
            return CommandValidation.Valid(key);
        }
        catch (AntiforgeryValidationException)
        {
            return CommandValidation.Failed(Problem(
                StatusCodes.Status400BadRequest,
                "Antiforgery validation failed",
                "A valid antiforgery cookie and request token are required.",
                "identities_and_capabilities.antiforgery_invalid"));
        }
    }

    private static IResult MapResult(InstallationRecoveryFactorResult result) =>
        result.Outcome switch
        {
            InstallationRecoveryFactorOutcome.Succeeded => Results.Ok(result.Response),
            InstallationRecoveryFactorOutcome.InvalidRecoveryFactor => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid recovery factor",
                "The recovery factor must be canonical Base64URL encoding of 32 bytes.",
                "installation_recovery.invalid_recovery_factor"),
            InstallationRecoveryFactorOutcome.AuthenticationRequired => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            InstallationRecoveryFactorOutcome.GeneralConfigurationRequired => Problem(
                StatusCodes.Status403Forbidden,
                "General Configuration required",
                "A current General Configuration responsibility is required.",
                "identities_and_capabilities.general_configuration_required"),
            InstallationRecoveryFactorOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used",
                "The supplied Idempotency-Key belongs to another actor or intention.",
                "identities_and_capabilities.idempotency_conflict"),
            InstallationRecoveryFactorOutcome.GenerationExhausted => Problem(
                StatusCodes.Status409Conflict,
                "Recovery factor generation exhausted",
                "The recovery factor generation cannot advance further.",
                "installation_recovery.generation_exhausted"),
            InstallationRecoveryFactorOutcome.InconsistentInstallationState => Problem(
                StatusCodes.Status500InternalServerError,
                "Installation recovery state is inconsistent",
                "Recovery factor establishment cannot proceed for this installation.",
                "installation_recovery.inconsistent_installation_state"),
            _ => throw new UnreachableException()
        };

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

    private sealed record CommandValidation(Guid Key, IResult? Error)
    {
        internal static CommandValidation Valid(Guid key) => new(key, null);

        internal static CommandValidation Failed(IResult error) => new(Guid.Empty, error);
    }
}
