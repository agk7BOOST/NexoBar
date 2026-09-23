using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NexoBar.Inventory;

public static class InventoryModule
{
    public static IServiceCollection AddInventory(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Inventory")
            ?? throw new InvalidOperationException(
                "Connection string 'Inventory' is required.");

        services.AddDbContext<InventoryDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "inventory")));
        services.AddScoped<InventoryService>();
        services.AddScoped<InventoryCountService>();
        services.AddScoped<InventoryMovementService>();
        services.AddScoped<InventoryMovementCorrectionService>();
        services.AddScoped<InventoryMovementHistoryService>();
        services.AddScoped<InventoryLifecycleService>();
        services.AddScoped<InventoryDeleteService>();
        services.AddInventoryOperationInvalidationPublisher();
        return services;
    }

    public static IEndpointRouteBuilder MapInventoryEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/inventory/items", CreateItemAsync)
            .WithName("CreateInventoryItem")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Accepts<CreateInventoryItemRequest>("application/json")
            .Produces<InventoryItemResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapPost(
                "/api/inventory/items/{itemId}/retire",
                RetireItemAsync)
            .WithName("RetireInventoryItem")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Accepts<InventoryLifecycleRequest>("application/json")
            .Produces<InventoryLifecycleResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapPost(
                "/api/inventory/items/{itemId}/reactivate",
                ReactivateItemAsync)
            .WithName("ReactivateInventoryItem")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Accepts<ReactivateInventoryItemRequest>("application/json")
            .Produces<InventoryLifecycleResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapPost(
                "/api/inventory/items/{itemId}/unit-corrections",
                CorrectUnitAsync)
            .WithName("CorrectInventoryUnit")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Accepts<CorrectInventoryUnitRequest>("application/json")
            .Produces<InventoryUnitCorrectionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapPost(
                "/api/inventory/items/{itemId}/delete",
                DeleteItemAsync)
            .WithName("DeleteInventoryItem")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Produces<InventoryItemDeleteResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapPost(
                "/api/inventory/items/{itemId}/counts",
                RecordCountAsync)
            .WithName("RecordInventoryCount")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Accepts<RecordInventoryCountRequest>("application/json")
            .Produces<CountObservationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapPost(
                "/api/inventory/items/{itemId}/reconcile",
                ReconcileCountAsync)
            .WithName("ReconcileInventoryCount")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Accepts<ReconcileInventoryCountRequest>("application/json")
            .Produces<ReconcileInventoryCountResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        MapQuantityMovementEndpoint(
            endpoints,
            "/api/inventory/items/{itemId}/entries",
            "RecordInventoryEntry",
            InventoryMovementCommand.RecordEntryCommandKind);
        MapQuantityMovementEndpoint(
            endpoints,
            "/api/inventory/items/{itemId}/manual-exits",
            "RecordManualInventoryExit",
            InventoryMovementCommand.RecordManualExitCommandKind);
        MapQuantityMovementEndpoint(
            endpoints,
            "/api/inventory/items/{itemId}/waste",
            "RecordInventoryWaste",
            InventoryMovementCommand.RecordWasteCommandKind);

        endpoints.MapGet(
                "/api/inventory/items/{itemId}/movements",
                ReadMovementHistoryAsync)
            .WithName("ReadInventoryMovementHistory")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Produces<InventoryMovementHistoryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        endpoints.MapPost("/api/inventory/movements/{rootMovementId}/corrections", CorrectMovementAsync)
            .WithName("CorrectInventoryMovement").WithTags("Inventory").RequireAuthorization()
            .Accepts<CorrectInventoryMovementRequest>("application/json")
            .Produces<InventoryMovementCorrectionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapGet(
                "/api/inventory/configuration/items",
                ListConfigurationItemsAsync)
            .WithName("ListInventoryConfigurationItems")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Produces<IReadOnlyList<InventoryConfigurationItemResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(
                "/api/inventory/operations/items",
                ListOperationalItemsAsync)
            .WithName("ListInventoryOperationalItems")
            .WithTags("Inventory")
            .RequireAuthorization()
            .Produces<IReadOnlyList<InventoryOperationalItemResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> CorrectMovementAsync(
        string rootMovementId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        CorrectInventoryMovementRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        InventoryMovementCorrectionService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(rootMovementId, out var rootId) || rootId == Guid.Empty) return InvalidItemIdProblem();
        if (!TryParseIdempotencyKey(idempotencyKey, out var key)) return IdempotencyKeyProblem(idempotencyKey);
        if (request.ExpectedMovementRevision is null || request.ExpectedMovementRevision < 1 || request.CorrectedNature is not ("Entry" or "ManualExit" or "Waste") || !InventoryQuantity.TryParseNonNegative(request.CorrectedQuantity, out var quantity, out _))
            return Problem(StatusCodes.Status400BadRequest, "Invalid movement correction", "A valid correctedNature, correctedQuantity and expectedMovementRevision are required.", "inventory.movement_correction.request_invalid");
        var antiforgeryProblem = await ValidateAntiforgeryAsync(httpContext, antiforgery, "inventory.movement_correction.antiforgery_invalid");
        if (antiforgeryProblem is not null) return antiforgeryProblem;
        var result = await service.CorrectAsync(key, rootId, request.CorrectedNature, quantity, request.ExpectedMovementRevision.Value, cancellationToken);
        return result.Outcome switch
        {
            "succeeded" => Results.Ok(result.Response),
            "unauthenticated" => InvalidSessionProblem(),
            "forbidden" => InventoryOperationForbiddenProblem(),
            "item_not_found" => InventoryItemNotFoundProblem(),
            "invalid_root" => Problem(StatusCodes.Status409Conflict, "Movement cannot be corrected", "The target must be an ordinary root Movement.", "inventory.movement_correction.invalid_root"),
            "stale" => Problem(StatusCodes.Status409Conflict, "Movement changed concurrently", "Reload History before correcting this Movement.", "inventory.movement_correction.stale"),
            "idempotency_conflict" => Problem(StatusCodes.Status409Conflict, "Idempotency-Key conflict", "The key was committed for another actor or intention.", "inventory.movement_correction.idempotency_conflict"),
            "out_of_range" => Problem(StatusCodes.Status409Conflict, "Result out of range", "The correction would exceed the supported quantity range.", "inventory.movement_correction.result_out_of_range"),
            _ => throw new UnreachableException()
        };
    }

    private static void MapQuantityMovementEndpoint(
        IEndpointRouteBuilder endpoints,
        string pattern,
        string endpointName,
        string commandKind)
    {
        endpoints.MapPost(
                pattern,
                (string itemId,
                    [FromHeader(Name = "Idempotency-Key"), Required]
                    string? idempotencyKey,
                    RecordInventoryMovementRequest request,
                    HttpContext httpContext,
                    IAntiforgery antiforgery,
                    InventoryMovementService service,
                    CancellationToken cancellationToken) =>
                    RecordQuantityMovementAsync(
                        itemId,
                        idempotencyKey,
                        request,
                        commandKind,
                        httpContext,
                        antiforgery,
                        service,
                        cancellationToken))
            .WithName(endpointName)
            .WithTags("Inventory")
            .RequireAuthorization()
            .Accepts<RecordInventoryMovementRequest>("application/json")
            .Produces<InventoryMovementResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> CreateItemAsync(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        CreateInventoryItemRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        InventoryService service,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Inventory Item creation requires an Idempotency-Key containing a UUID v4.",
                "inventory.item.idempotency_key_required");
        }

        if (!Guid.TryParse(idempotencyKey, out var commandId) ||
            !IsUuidVersion4(commandId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "inventory.item.idempotency_key_invalid");
        }

        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Antiforgery validation failed",
                "A valid antiforgery cookie and request token are required.",
                "inventory.item.antiforgery_invalid");
        }

        var result = await service.CreateItemAsync(
            commandId,
            request,
            cancellationToken);
        return result.Outcome switch
        {
            CreateInventoryItemOutcome.Created => Results.Created(
                $"/api/inventory/items/{result.Item!.ItemId}",
                result.Item),
            CreateInventoryItemOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            CreateInventoryItemOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Inventory configuration forbidden",
                "The current Identity is not authorized to configure Inventory.",
                "inventory.configuration.forbidden"),
            CreateInventoryItemOutcome.InvalidName => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid operational name",
                "operationalName must be non-empty, one line, and at most 200 characters.",
                "inventory.item.operational_name_invalid"),
            CreateInventoryItemOutcome.InvalidUnit => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid operational unit",
                "operationalUnit must be non-empty, one line, and at most 100 characters.",
                "inventory.item.operational_unit_invalid"),
            CreateInventoryItemOutcome.DuplicateName => Problem(
                StatusCodes.Status409Conflict,
                "Operational name already in use",
                "An Inventory Item already uses that operational name, ignoring case.",
                "inventory.item.operational_name_conflict"),
            CreateInventoryItemOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Inventory Item creation.",
                "inventory.item.idempotency_key_conflict"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> RetireItemAsync(
        string itemId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        InventoryLifecycleRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        InventoryLifecycleService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(itemId, out var inventoryItemId) ||
            inventoryItemId == Guid.Empty)
        {
            return InvalidItemIdProblem();
        }

        if (!TryParseIdempotencyKey(idempotencyKey, out var commandId))
        {
            return IdempotencyKeyProblem(idempotencyKey);
        }

        if (request.ExpectedCurrentIsActive is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid lifecycle intention",
                "expectedCurrentIsActive is required.",
                "inventory.lifecycle.expected_current_is_active_invalid");
        }

        var antiforgeryProblem = await ValidateAntiforgeryAsync(
            httpContext,
            antiforgery,
            "inventory.lifecycle.antiforgery_invalid");
        if (antiforgeryProblem is not null)
        {
            return antiforgeryProblem;
        }

        var result = await service.RetireAsync(
            commandId,
            inventoryItemId,
            request.ExpectedCurrentIsActive.Value,
            cancellationToken);
        return MapLifecycleResult(result, "retire");
    }

    private static async Task<IResult> ReactivateItemAsync(
        string itemId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        ReactivateInventoryItemRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        InventoryLifecycleService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(itemId, out var inventoryItemId) ||
            inventoryItemId == Guid.Empty)
        {
            return InvalidItemIdProblem();
        }

        if (!TryParseIdempotencyKey(idempotencyKey, out var commandId))
        {
            return IdempotencyKeyProblem(idempotencyKey);
        }

        if (request.ExpectedCurrentIsActive is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid lifecycle intention",
                "expectedCurrentIsActive is required.",
                "inventory.lifecycle.expected_current_is_active_invalid");
        }

        var antiforgeryProblem = await ValidateAntiforgeryAsync(
            httpContext,
            antiforgery,
            "inventory.lifecycle.antiforgery_invalid");
        if (antiforgeryProblem is not null)
        {
            return antiforgeryProblem;
        }

        var result = await service.ReactivateAsync(
            commandId,
            inventoryItemId,
            request.ExpectedCurrentIsActive.Value,
            request.NewOperationalName,
            cancellationToken);
        return MapLifecycleResult(result, "reactivate");
    }

    private static async Task<IResult> CorrectUnitAsync(
        string itemId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        CorrectInventoryUnitRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        InventoryLifecycleService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(itemId, out var inventoryItemId) ||
            inventoryItemId == Guid.Empty)
        {
            return InvalidItemIdProblem();
        }

        if (!TryParseIdempotencyKey(idempotencyKey, out var commandId))
        {
            return IdempotencyKeyProblem(idempotencyKey);
        }

        var antiforgeryProblem = await ValidateAntiforgeryAsync(
            httpContext,
            antiforgery,
            "inventory.unit_correction.antiforgery_invalid");
        if (antiforgeryProblem is not null)
        {
            return antiforgeryProblem;
        }

        var result = await service.CorrectUnitAsync(
            commandId,
            inventoryItemId,
            request.ExpectedCurrentUnit,
            request.NewUnit,
            cancellationToken);
        return result.Outcome switch
        {
            InventoryUnitCorrectionOutcome.Succeeded => Results.Ok(result.Response),
            InventoryUnitCorrectionOutcome.Unauthenticated => InvalidSessionProblem(),
            InventoryUnitCorrectionOutcome.Forbidden => InventoryConfigurationForbiddenProblem(),
            InventoryUnitCorrectionOutcome.NotFound => InventoryItemNotFoundProblem(),
            InventoryUnitCorrectionOutcome.InvalidUnit => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid operational unit",
                "expectedCurrentUnit and newUnit must be non-empty, one-line values of at most 100 characters.",
                "inventory.item.operational_unit_invalid"),
            InventoryUnitCorrectionOutcome.UnitConcurrencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Inventory Unit changed concurrently",
                "The current operational Unit does not match expectedCurrentUnit.",
                "inventory.unit_correction.concurrency_conflict",
                currentUnit: result.CurrentUnit),
            InventoryUnitCorrectionOutcome.HistoryExists => Problem(
                StatusCodes.Status409Conflict,
                "Inventory Unit correction requires replacement",
                "The Element has Movement History. Retire it and create a new Element with the corrected Unit.",
                "inventory.item.unit_correction_requires_replacement"),
            InventoryUnitCorrectionOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Inventory Unit correction.",
                "inventory.unit_correction.idempotency_key_conflict"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> DeleteItemAsync(
        string itemId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        InventoryDeleteService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(itemId, out var inventoryItemId) ||
            inventoryItemId == Guid.Empty)
        {
            return InvalidItemIdProblem();
        }

        if (!TryParseIdempotencyKey(idempotencyKey, out var commandId))
        {
            return IdempotencyKeyProblem(idempotencyKey);
        }

        var antiforgeryProblem = await ValidateAntiforgeryAsync(
            httpContext,
            antiforgery,
            "inventory.delete.antiforgery_invalid");
        if (antiforgeryProblem is not null)
        {
            return antiforgeryProblem;
        }

        var result = await service.DeleteAsync(
            commandId,
            inventoryItemId,
            cancellationToken);
        return result.Outcome switch
        {
            InventoryDeleteCommandOutcome.Deleted => Results.Ok(result.Response),
            InventoryDeleteCommandOutcome.Unauthenticated => InvalidSessionProblem(),
            InventoryDeleteCommandOutcome.Forbidden => InventoryConfigurationForbiddenProblem(),
            InventoryDeleteCommandOutcome.NotFound => InventoryItemNotFoundProblem(),
            InventoryDeleteCommandOutcome.MovementHistoryConflict => Problem(
                StatusCodes.Status409Conflict,
                "Inventory Item has Movement History",
                "The Inventory Item must be retained because Movement History exists.",
                "inventory.item.delete_movement_history_conflict"),
            InventoryDeleteCommandOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Inventory Item deletion.",
                "inventory.item.delete.idempotency_key_conflict"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ListConfigurationItemsAsync(
        InventoryService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListConfigurationItemsAsync(cancellationToken);
        return MapReadResult(
            result,
            "Inventory configuration forbidden",
            "The current Identity is not authorized to configure Inventory.",
            "inventory.configuration.forbidden");
    }

    private static async Task<IResult> RecordCountAsync(
        string itemId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        RecordInventoryCountRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        InventoryCountService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(itemId, out var inventoryItemId) ||
            inventoryItemId == Guid.Empty)
        {
            return InvalidItemIdProblem();
        }

        if (!TryParseIdempotencyKey(idempotencyKey, out var commandId))
        {
            return IdempotencyKeyProblem(idempotencyKey);
        }

        if (!InventoryQuantity.TryParseObserved(
                request.ObservedQuantity,
                out var observedQuantity,
                out _))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid observed quantity",
                "observedQuantity must be a non-negative invariant decimal string with at most 16 integral and 12 fractional digits.",
                "inventory.count.observed_quantity_invalid");
        }

        var antiforgeryProblem = await ValidateAntiforgeryAsync(
            httpContext,
            antiforgery,
            "inventory.count.antiforgery_invalid");
        if (antiforgeryProblem is not null)
        {
            return antiforgeryProblem;
        }

        var result = await service.RecordAsync(
            commandId,
            inventoryItemId,
            observedQuantity,
            cancellationToken);
        return result.Outcome switch
        {
            RecordInventoryCountOutcome.Recorded => Results.Created(
                $"/api/inventory/items/{inventoryItemId}/counts/{result.Response!.CountObservationId}",
                result.Response),
            RecordInventoryCountOutcome.Unauthenticated => InvalidSessionProblem(),
            RecordInventoryCountOutcome.Forbidden => InventoryOperationForbiddenProblem(),
            RecordInventoryCountOutcome.ItemNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Inventory Item not found",
                "The requested Inventory Item does not exist.",
                "inventory.item.not_found"),
            RecordInventoryCountOutcome.ItemRetired => Problem(
                StatusCodes.Status409Conflict,
                "Inventory Item is retired",
                "Retired Inventory Items cannot receive Counts.",
                "inventory.item.retired"),
            RecordInventoryCountOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Inventory Count.",
                "inventory.count.idempotency_key_conflict"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ReconcileCountAsync(
        string itemId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        ReconcileInventoryCountRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        InventoryCountService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(itemId, out var inventoryItemId) ||
            inventoryItemId == Guid.Empty)
        {
            return InvalidItemIdProblem();
        }

        if (!TryParseIdempotencyKey(idempotencyKey, out var commandId))
        {
            return IdempotencyKeyProblem(idempotencyKey);
        }

        if (request.CountObservationId == Guid.Empty)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Count Observation",
                "countObservationId must contain a non-empty UUID.",
                "inventory.reconciliation.count_observation_id_invalid");
        }

        var antiforgeryProblem = await ValidateAntiforgeryAsync(
            httpContext,
            antiforgery,
            "inventory.reconciliation.antiforgery_invalid");
        if (antiforgeryProblem is not null)
        {
            return antiforgeryProblem;
        }

        var result = await service.ReconcileAsync(
            commandId,
            inventoryItemId,
            request.CountObservationId,
            cancellationToken);
        return result.Outcome switch
        {
            ReconcileInventoryCountOutcome.Succeeded => Results.Ok(result.Response),
            ReconcileInventoryCountOutcome.Unauthenticated => InvalidSessionProblem(),
            ReconcileInventoryCountOutcome.Forbidden => InventoryOperationForbiddenProblem(),
            ReconcileInventoryCountOutcome.ItemNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Inventory Item not found",
                "The requested Inventory Item does not exist.",
                "inventory.item.not_found"),
            ReconcileInventoryCountOutcome.ItemRetired => Problem(
                StatusCodes.Status409Conflict,
                "Inventory Item is retired",
                "Retired Inventory Items cannot be reconciled.",
                "inventory.item.retired"),
            ReconcileInventoryCountOutcome.ObservationNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Count Observation not found",
                "The Count Observation does not exist for the requested Inventory Item.",
                "inventory.count_observation.not_found"),
            ReconcileInventoryCountOutcome.CountInvalidated => Problem(
                StatusCodes.Status409Conflict,
                "Count invalidated",
                "La existencia cambió después del conteo. Realiza una nueva verificación física.",
                "inventory.reconciliation.count_invalidated"),
            ReconcileInventoryCountOutcome.ConfigurationChanged => Problem(
                StatusCodes.Status409Conflict,
                "Observation invalidated by configuration change",
                "The Inventory Item unit changed after the Count. Record a new physical Count.",
                "inventory.reconciliation.observation_invalidated"),
            ReconcileInventoryCountOutcome.ObservationInvalidated => Problem(
                StatusCodes.Status409Conflict,
                "Count invalidated",
                "The Count Observation is no longer usable after an Inventory configuration change.",
                "inventory.reconciliation.observation_invalidated"),
            ReconcileInventoryCountOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Inventory Reconciliation.",
                "inventory.reconciliation.idempotency_key_conflict"),
            ReconcileInventoryCountOutcome.RevisionOverflow => Problem(
                StatusCodes.Status500InternalServerError,
                "Inventory revision is inconsistent",
                "The Inventory Item movement revision cannot be advanced safely.",
                "inventory.reconciliation.revision_overflow"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> RecordQuantityMovementAsync(
        string itemId,
        string? idempotencyKey,
        RecordInventoryMovementRequest request,
        string commandKind,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        InventoryMovementService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(itemId, out var inventoryItemId) ||
            inventoryItemId == Guid.Empty)
        {
            return InvalidItemIdProblem();
        }

        if (!TryParseIdempotencyKey(idempotencyKey, out var commandId))
        {
            return IdempotencyKeyProblem(idempotencyKey);
        }

        if (!InventoryQuantity.TryParsePositive(
                request.Quantity,
                out var quantity,
                out _))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid movement quantity",
                "quantity must be a positive invariant decimal string with at most 16 integral and 12 fractional digits.",
                "inventory.movement.quantity_invalid");
        }

        var antiforgeryProblem = await ValidateAntiforgeryAsync(
            httpContext,
            antiforgery,
            "inventory.movement.antiforgery_invalid");
        if (antiforgeryProblem is not null)
        {
            return antiforgeryProblem;
        }

        var result = commandKind switch
        {
            InventoryMovementCommand.RecordEntryCommandKind =>
                await service.RecordEntryAsync(
                    commandId,
                    inventoryItemId,
                    quantity,
                    cancellationToken),
            InventoryMovementCommand.RecordManualExitCommandKind =>
                await service.RecordManualExitAsync(
                    commandId,
                    inventoryItemId,
                    quantity,
                    cancellationToken),
            InventoryMovementCommand.RecordWasteCommandKind =>
                await service.RecordWasteAsync(
                    commandId,
                    inventoryItemId,
                    quantity,
                    cancellationToken),
            _ => throw new UnreachableException()
        };

        return result.Outcome switch
        {
            RecordInventoryMovementOutcome.Succeeded => Results.Ok(result.Response),
            RecordInventoryMovementOutcome.Unauthenticated => InvalidSessionProblem(),
            RecordInventoryMovementOutcome.Forbidden =>
                InventoryOperationForbiddenProblem(),
            RecordInventoryMovementOutcome.ItemNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Inventory Item not found",
                "The requested Inventory Item does not exist.",
                "inventory.item.not_found"),
            RecordInventoryMovementOutcome.Retired => Problem(
                StatusCodes.Status409Conflict,
                "Inventory Item is retired",
                "Retired Inventory Items cannot receive ordinary Movements.",
                "inventory.item.retired"),
            RecordInventoryMovementOutcome.QuantityNotEstablished => Problem(
                StatusCodes.Status409Conflict,
                "Inventory quantity not established",
                "Current physical existence must be established through Count and Reconciliation before an ordinary Movement.",
                "inventory.item.reconciliation_required"),
            RecordInventoryMovementOutcome.ResultOutOfRange => Problem(
                StatusCodes.Status409Conflict,
                "Inventory quantity is outside the supported range",
                "The resulting registered quantity cannot be represented exactly.",
                "inventory.movement.result_out_of_range"),
            RecordInventoryMovementOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Inventory Movement.",
                "inventory.movement.idempotency_key_conflict"),
            RecordInventoryMovementOutcome.RevisionOverflow => Problem(
                StatusCodes.Status500InternalServerError,
                "Inventory revision is inconsistent",
                "The Inventory Item movement revision cannot be advanced safely.",
                "inventory.movement.revision_overflow"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ListOperationalItemsAsync(
        InventoryService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListOperationalItemsAsync(cancellationToken);
        return MapReadResult(
            result,
            "Inventory operation forbidden",
            "The current Identity is not authorized to operate Inventory.",
            "inventory.operation.forbidden");
    }

    private static async Task<IResult> ReadMovementHistoryAsync(
        string itemId,
        [FromQuery] string? beforeRevision,
        [FromQuery] string? limit,
        InventoryMovementHistoryService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(itemId, out var inventoryItemId) ||
            inventoryItemId == Guid.Empty)
        {
            return InvalidItemIdProblem();
        }

        if (beforeRevision is not null &&
            (!long.TryParse(
                beforeRevision,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedBeforeRevision) ||
                parsedBeforeRevision <= 0))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid movement revision cursor",
                "beforeRevision must be a positive integer.",
                "inventory.movement_history.before_revision_invalid");
        }

        long? cursor = beforeRevision is null
            ? null
            : long.Parse(
                beforeRevision,
                System.Globalization.CultureInfo.InvariantCulture);
        if (limit is not null &&
            (!int.TryParse(
                limit,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedLimit) ||
                parsedLimit is < 1 or > 100))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid movement page limit",
                "limit must be an integer between 1 and 100.",
                "inventory.movement_history.limit_invalid");
        }

        var pageLimit = limit is null
            ? 50
            : int.Parse(limit, System.Globalization.CultureInfo.InvariantCulture);
        var result = await service.ReadAsync(
            inventoryItemId,
            cursor,
            pageLimit,
            cancellationToken);
        return result.Outcome switch
        {
            InventoryMovementHistoryOutcome.Succeeded => Results.Ok(result.Response),
            InventoryMovementHistoryOutcome.Unauthenticated => InvalidSessionProblem(),
            InventoryMovementHistoryOutcome.Forbidden =>
                InventoryOperationForbiddenProblem(),
            InventoryMovementHistoryOutcome.ItemNotFound => Problem(
                StatusCodes.Status404NotFound,
                "Inventory Item not found",
                "The requested Inventory Item does not exist.",
                "inventory.item.not_found"),
            InventoryMovementHistoryOutcome.ActorMissing => Problem(
                StatusCodes.Status500InternalServerError,
                "Inventory Movement actor is inconsistent",
                "A historical Inventory Movement references an unavailable Identity.",
                "inventory.movement_history.actor_missing"),
            InventoryMovementHistoryOutcome.StateInconsistent => Problem(
                StatusCodes.Status500InternalServerError,
                "Inventory Movement History is inconsistent",
                "A historical Inventory Movement cannot be represented safely.",
                "inventory.movement_history.state_inconsistent"),
            _ => throw new UnreachableException()
        };
    }

    private static IResult MapReadResult<TResponse>(
        InventoryReadResult<TResponse> result,
        string forbiddenTitle,
        string forbiddenDetail,
        string forbiddenCode) =>
        result.Outcome switch
        {
            InventoryReadOutcome.Succeeded => Results.Ok(result.Items),
            InventoryReadOutcome.Unauthenticated => Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid session",
                "The current session is invalid or expired.",
                "identities_and_capabilities.invalid_session"),
            InventoryReadOutcome.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                forbiddenTitle,
                forbiddenDetail,
                forbiddenCode),
            _ => throw new UnreachableException()
        };

    private static IResult MapLifecycleResult(
        InventoryLifecycleCommandResult result,
        string operation) =>
        result.Outcome switch
        {
            InventoryLifecycleCommandOutcome.Changed => Results.Ok(result.Response),
            InventoryLifecycleCommandOutcome.Unauthenticated => InvalidSessionProblem(),
            InventoryLifecycleCommandOutcome.Forbidden => InventoryConfigurationForbiddenProblem(),
            InventoryLifecycleCommandOutcome.NotFound => InventoryItemNotFoundProblem(),
            InventoryLifecycleCommandOutcome.AlreadyRetired => Problem(
                StatusCodes.Status409Conflict,
                "Inventory Item is already retired",
                "The requested Inventory Item is already retired.",
                "inventory.item.already_retired"),
            InventoryLifecycleCommandOutcome.AlreadyActive => Problem(
                StatusCodes.Status409Conflict,
                "Inventory Item is already active",
                "The requested Inventory Item is already active.",
                "inventory.item.already_active"),
            InventoryLifecycleCommandOutcome.LifecycleConcurrencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Inventory lifecycle changed concurrently",
                "The current lifecycle state does not match expectedCurrentIsActive.",
                "inventory.lifecycle.concurrency_conflict",
                currentIsActive: result.CurrentIsActive),
            InventoryLifecycleCommandOutcome.InvalidName => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid operational name",
                "newOperationalName must be non-empty, one line, and at most 200 characters.",
                "inventory.item.operational_name_invalid"),
            InventoryLifecycleCommandOutcome.NameConflict => Problem(
                StatusCodes.Status409Conflict,
                "Operational name already in use",
                "An active Inventory Item already uses that operational name, ignoring case.",
                "inventory.item.reactivation_name_conflict"),
            InventoryLifecycleCommandOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                $"The supplied Idempotency-Key identifies an incompatible Inventory {operation} command.",
                $"inventory.{operation}.idempotency_key_conflict"),
            _ => throw new UnreachableException()
        };

    private static IResult Problem(
        int statusCode,
        string title,
        string detail,
        string code,
        string? currentUnit = null,
        bool? currentIsActive = null) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["currentUnit"] = currentUnit,
                ["currentIsActive"] = currentIsActive
            });

    private static bool TryParseIdempotencyKey(string? value, out Guid key) =>
        Guid.TryParse(value, out key) && IsUuidVersion4(key);

    private static IResult IdempotencyKeyProblem(string? value) =>
        Problem(
            StatusCodes.Status400BadRequest,
            value is null ? "Idempotency-Key is required" : "Invalid Idempotency-Key",
            "Idempotency-Key must contain a UUID v4.",
            value is null
                ? "inventory.idempotency_key_required"
                : "inventory.idempotency_key_invalid");

    private static async Task<IResult?> ValidateAntiforgeryAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        string code)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Antiforgery validation failed",
                "A valid antiforgery cookie and request token are required.",
                code);
        }
    }

    private static IResult InvalidSessionProblem() => Problem(
        StatusCodes.Status401Unauthorized,
        "Invalid session",
        "The current session is invalid or expired.",
        "identities_and_capabilities.invalid_session");

    private static IResult InventoryOperationForbiddenProblem() => Problem(
        StatusCodes.Status403Forbidden,
        "Inventory operation forbidden",
        "The current Identity is not authorized to operate Inventory.",
        "inventory.operation.forbidden");

    private static IResult InventoryConfigurationForbiddenProblem() => Problem(
        StatusCodes.Status403Forbidden,
        "Inventory configuration forbidden",
        "The current Identity is not authorized to configure Inventory.",
        "inventory.configuration.forbidden");

    private static IResult InventoryItemNotFoundProblem() => Problem(
        StatusCodes.Status404NotFound,
        "Inventory Item not found",
        "The requested Inventory Item does not exist.",
        "inventory.item.not_found");

    private static IResult InvalidItemIdProblem() => Problem(
        StatusCodes.Status400BadRequest,
        "Invalid Inventory Item identifier",
        "itemId must contain a non-empty UUID.",
        "inventory.item.id_invalid");

    private static bool IsUuidVersion4(Guid value)
    {
        var bytes = value.ToByteArray(bigEndian: true);
        return bytes[6] >> 4 == 4 && (bytes[8] & 0xc0) == 0x80;
    }
}
