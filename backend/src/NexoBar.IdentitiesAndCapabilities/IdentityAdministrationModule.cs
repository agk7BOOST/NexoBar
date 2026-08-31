using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace NexoBar.IdentitiesAndCapabilities;

public static class IdentityAdministrationModule
{
    public static IEndpointRouteBuilder MapIdentityAdministrationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/identities")
            .RequireAuthorization()
            .WithTags("Identity Administration");

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListIdentities")
            .Produces<IReadOnlyList<IdentityAdministrationResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreateIdentity")
            .Accepts<CreateIdentityRequest>("application/json")
            .Produces<IdentityAdministrationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{identityId:guid}/change-operational-name", ChangeNameAsync)
            .WithName("ChangeIdentityOperationalName")
            .Accepts<ChangeIdentityOperationalNameRequest>("application/json")
            .ProducesAdministrationResults();
        group.MapPost("/{identityId:guid}/activate", ActivateAsync)
            .WithName("ActivateIdentity")
            .ProducesAdministrationResults();
        group.MapPost("/{identityId:guid}/deactivate", DeactivateAsync)
            .WithName("DeactivateIdentity")
            .ProducesAdministrationResults();
        group.MapPost("/{identityId:guid}/credential", SetCredentialAsync)
            .WithName("SetIdentityLocalCredential")
            .Accepts<SetLocalCredentialRequest>("application/json")
            .ProducesAdministrationResults();
        group.MapPost(
                "/{identityId:guid}/responsibilities/{code}/assign",
                AssignResponsibilityAsync)
            .WithName("AssignIdentityResponsibility")
            .ProducesAdministrationResults();
        group.MapPost(
                "/{identityId:guid}/responsibilities/{code}/revoke",
                RevokeResponsibilityAsync)
            .WithName("RevokeIdentityResponsibility")
            .ProducesAdministrationResults();
        group.MapPost(
                "/{identityId:guid}/preparation-enablement/{responsibilityId:guid}/grant",
                GrantEnablementAsync)
            .WithName("GrantIdentityPreparationEnablement")
            .ProducesAdministrationResults();
        group.MapPost(
                "/{identityId:guid}/preparation-enablement/{responsibilityId:guid}/revoke",
                RevokeEnablementAsync)
            .WithName("RevokeIdentityPreparationEnablement")
            .ProducesAdministrationResults();

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        IdentityAdministrationService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListAsync(cancellationToken);
        return result.Outcome switch
        {
            IdentityAdministrationOutcome.Succeeded => Results.Ok(result.Identities),
            IdentityAdministrationOutcome.AuthenticationRequired => AuthenticationRequired(),
            IdentityAdministrationOutcome.GeneralConfigurationRequired =>
                GeneralConfigurationRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> CreateAsync(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        CreateIdentityRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentityAdministrationService service,
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

        var result = await service.CreateIdentityAsync(
            validation.Key,
            request,
            cancellationToken);
        return MapResult(result, created: true);
    }

    private static async Task<IResult> ChangeNameAsync(
        Guid identityId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        ChangeIdentityOperationalNameRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentityAdministrationService service,
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

        return MapResult(await service.ChangeOperationalNameAsync(
            validation.Key,
            identityId,
            request,
            cancellationToken));
    }

    private static async Task<IResult> ActivateAsync(
        Guid identityId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentityAdministrationService service,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateCommandAsync(
            idempotencyKey,
            httpContext,
            antiforgery);
        return validation.Error ?? MapResult(await service.ActivateAsync(
            validation.Key,
            identityId,
            cancellationToken));
    }

    private static async Task<IResult> DeactivateAsync(
        Guid identityId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentityAdministrationService service,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateCommandAsync(
            idempotencyKey,
            httpContext,
            antiforgery);
        return validation.Error ?? MapResult(await service.DeactivateAsync(
            validation.Key,
            identityId,
            cancellationToken));
    }

    private static async Task<IResult> SetCredentialAsync(
        Guid identityId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        SetLocalCredentialRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentityAdministrationService service,
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

        return MapResult(await service.SetCredentialAsync(
            validation.Key,
            identityId,
            request,
            cancellationToken));
    }

    private static Task<IResult> AssignResponsibilityAsync(
        Guid identityId,
        string code,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentityAdministrationService service,
        CancellationToken cancellationToken) =>
        ChangeResponsibilityAsync(
            identityId,
            code,
            idempotencyKey,
            httpContext,
            antiforgery,
            service.AssignResponsibilityAsync,
            cancellationToken);

    private static Task<IResult> RevokeResponsibilityAsync(
        Guid identityId,
        string code,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentityAdministrationService service,
        CancellationToken cancellationToken) =>
        ChangeResponsibilityAsync(
            identityId,
            code,
            idempotencyKey,
            httpContext,
            antiforgery,
            service.RevokeResponsibilityAsync,
            cancellationToken);

    private static async Task<IResult> ChangeResponsibilityAsync(
        Guid identityId,
        string code,
        string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        Func<Guid, Guid, FunctionalResponsibility, CancellationToken,
            Task<IdentityAdministrationResult>> change,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<FunctionalResponsibility>(
                code,
                ignoreCase: false,
                out var responsibility) ||
            !Enum.IsDefined(responsibility))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Functional Responsibility",
                "The responsibility code is not part of the closed repertoire.",
                "identities_and_capabilities.responsibility_code_invalid");
        }

        var validation = await ValidateCommandAsync(
            idempotencyKey,
            httpContext,
            antiforgery);
        if (validation.Error is not null)
        {
            return validation.Error;
        }

        return MapResult(await change(
            validation.Key,
            identityId,
            responsibility,
            cancellationToken));
    }

    private static Task<IResult> GrantEnablementAsync(
        Guid identityId,
        Guid responsibilityId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentityAdministrationService service,
        CancellationToken cancellationToken) =>
        ChangeEnablementAsync(
            identityId,
            responsibilityId,
            idempotencyKey,
            httpContext,
            antiforgery,
            service.GrantPreparationEnablementAsync,
            cancellationToken);

    private static Task<IResult> RevokeEnablementAsync(
        Guid identityId,
        Guid responsibilityId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentityAdministrationService service,
        CancellationToken cancellationToken) =>
        ChangeEnablementAsync(
            identityId,
            responsibilityId,
            idempotencyKey,
            httpContext,
            antiforgery,
            service.RevokePreparationEnablementAsync,
            cancellationToken);

    private static async Task<IResult> ChangeEnablementAsync(
        Guid identityId,
        Guid responsibilityId,
        string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        Func<Guid, Guid, Guid, CancellationToken,
            Task<IdentityAdministrationResult>> change,
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

        return MapResult(await change(
            validation.Key,
            identityId,
            responsibilityId,
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

    private static IResult MapResult(
        IdentityAdministrationResult result,
        bool created = false) =>
        result.Outcome switch
        {
            IdentityAdministrationOutcome.Succeeded when created => Results.Created(
                $"/api/identities/{result.Identity!.IdentityId:D}",
                result.Identity),
            IdentityAdministrationOutcome.Succeeded => Results.Ok(result.Identity),
            IdentityAdministrationOutcome.Invalid => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid administrative intention",
                $"The field '{result.InvalidField}' is invalid.",
                "identities_and_capabilities.invalid_request"),
            IdentityAdministrationOutcome.AuthenticationRequired => AuthenticationRequired(),
            IdentityAdministrationOutcome.GeneralConfigurationRequired =>
                GeneralConfigurationRequired(),
            IdentityAdministrationOutcome.NotFound => Problem(
                StatusCodes.Status404NotFound,
                "Identity not found",
                "The target Identity does not exist.",
                "identities_and_capabilities.identity_not_found"),
            IdentityAdministrationOutcome.PreparationResponsibilityNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Preparation Responsibility not found",
                "The target Preparation Responsibility does not exist.",
                "identities_and_capabilities.preparation_responsibility_not_found"),
            IdentityAdministrationOutcome.LastGeneralConfigurationPath => Problem(
                StatusCodes.Status409Conflict,
                "Last General Configuration path",
                "The command would remove the last current ordinary General Configuration path.",
                "identities_and_capabilities.last_general_configuration_path"),
            IdentityAdministrationOutcome.DuplicateLoginIdentifier => Problem(
                StatusCodes.Status409Conflict,
                "Login identifier already in use",
                "Another Identity already uses that login identifier.",
                "identities_and_capabilities.login_identifier_conflict"),
            IdentityAdministrationOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used",
                "The supplied Idempotency-Key belongs to another actor or intention.",
                "identities_and_capabilities.idempotency_conflict"),
            _ => throw new UnreachableException()
        };

    private static IResult AuthenticationRequired() => Problem(
        StatusCodes.Status401Unauthorized,
        "Invalid session",
        "The current session is invalid or expired.",
        "identities_and_capabilities.invalid_session");

    private static IResult GeneralConfigurationRequired() => Problem(
        StatusCodes.Status403Forbidden,
        "General Configuration required",
        "A current General Configuration responsibility is required.",
        "identities_and_capabilities.general_configuration_required");

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

    private static RouteHandlerBuilder ProducesAdministrationResults(
        this RouteHandlerBuilder builder) =>
        builder
            .Produces<IdentityAdministrationResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private sealed record CommandValidation(Guid Key, IResult? Error)
    {
        internal static CommandValidation Valid(Guid key) => new(key, null);

        internal static CommandValidation Failed(IResult error) =>
            new(Guid.Empty, error);
    }
}
