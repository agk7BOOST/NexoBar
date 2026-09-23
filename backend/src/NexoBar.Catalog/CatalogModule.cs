using System.Diagnostics;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.OperationalConfiguration;

namespace NexoBar.Catalog;

public static class CatalogModule
{
    public static IServiceCollection AddCatalog(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Catalog")
            ?? throw new InvalidOperationException(
                "Connection string 'Catalog' is required.");

        services.AddDbContext<CatalogDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "catalog")));
        services.AddScoped<CatalogService>();
        services.AddScoped<CatalogLifecycleService>();
        services.AddScoped<ProductDeleteService>();
        services.AddScoped<IOrderConfirmationCatalog, OrderConfirmationCatalog>();
        services.AddScoped<IOrderAppliedPriceCatalog, OrderConfirmationCatalog>();
        services.AddScoped<IProductOperationalReferenceLookup,
            ProductOperationalReferenceLookup>();

        return services;
    }

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/catalog/products")
            .RequireAuthorization()
            .WithTags("Catalog");

        group.MapPost(string.Empty, CreateProductAsync)
            .WithName("CreateCatalogProduct")
            .Accepts<CreateProductRequest>("application/json")
            .Produces<ProductResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet(string.Empty, ListActiveProductsAsync)
            .WithName("ListCatalogProducts")
            .Produces<IReadOnlyList<ProductResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}", FindActiveProductAsync)
            .WithName("GetCatalogProduct")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{productId:guid}/price-changes", ChangeProductPriceAsync)
            .WithName("ChangeCatalogProductPrice")
            .Accepts<ChangeProductPriceRequest>("application/json")
            .Produces<ProductPriceResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{productId:guid}/group-changes", ChangeProductGroupAsync)
            .Accepts<ChangeProductGroupRequest>("application/json")
            .Produces<ProductGroupResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{productId:guid}/operational-name-changes", ChangeProductOperationalNameAsync)
            .Accepts<ChangeProductOperationalNameRequest>("application/json")
            .Produces<ProductOperationalNameResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{productId:guid}/retire", RetireProductAsync)
            .Produces<ProductLifecycleResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{productId:guid}/reactivate", ReactivateProductAsync)
            .Produces<ProductLifecycleResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("/{productId:guid}", DeleteProductAsync)
            .WithName("DeleteCatalogProduct")
            .Produces<ProductDeleteResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{productId:guid}/availability-changes", ChangeProductAvailabilityAsync)
            .WithName("ChangeCatalogProductAvailability")
            .Accepts<ChangeProductAvailabilityRequest>("application/json")
            .Produces<ProductAvailabilityResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var groups = endpoints.MapGroup("/api/catalog/groups").RequireAuthorization().WithTags("Catalog");
        groups.MapGet(string.Empty, ListGroupsAsync).Produces<IReadOnlyList<GroupResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);
        groups.MapPost(string.Empty, CreateGroupAsync).Accepts<CreateGroupRequest>("application/json")
            .Produces<GroupResponse>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost(
                "/{productId:guid}/preparation-configuration-changes",
                ChangeProductPreparationConfigurationAsync)
            .WithName("ChangeCatalogProductPreparationConfiguration")
            .Accepts<ChangeProductPreparationConfigurationRequest>("application/json")
            .Produces<ProductPreparationConfigurationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapGroup("/api/catalog/operational-products")
            .RequireAuthorization()
            .WithTags("Catalog")
            .MapGet(string.Empty, ListOperationalProductsAsync)
            .WithName("ListOperationalCatalogProducts")
            .Produces<IReadOnlyList<OperationalProductResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGroup("/api/catalog/availability-administration-products")
            .RequireAuthorization()
            .WithTags("Catalog")
            .MapGet(string.Empty, ListAvailabilityAdministrationProductsAsync)
            .WithName("ListAvailabilityAdministrationProducts")
            .Produces<IReadOnlyList<AvailabilityAdministrationProductResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGroup("/api/catalog/preparation-responsibilities")
            .RequireAuthorization()
            .WithTags("Catalog")
            .MapGet(string.Empty, ListPreparationResponsibilitiesAsync)
            .WithName("ListCatalogPreparationResponsibilities")
            .Produces<IReadOnlyList<PreparationResponsibilityReference>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> ChangeProductPreparationConfigurationAsync(
        Guid productId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        ChangeProductPreparationConfigurationRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        CatalogService catalog,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Product preparation configuration change requires an Idempotency-Key containing a UUID v4.",
                "catalog.product.preparation_configuration.idempotency_key_required");
        }
        if (!Guid.TryParse(idempotencyKey, out var commandId) ||
            !IsUuidVersion4(commandId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "catalog.product.preparation_configuration.idempotency_key_invalid");
        }

        var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var result = await catalog.ChangeProductPreparationConfigurationAsync(
            commandId,
            productId,
            request,
            cancellationToken);

        return result.Outcome switch
        {
            ProductPreparationConfigurationChangeOutcome.Changed =>
                Results.Ok(result.Configuration),
            ProductPreparationConfigurationChangeOutcome.NotFound => Problem(
                StatusCodes.Status404NotFound,
                "Product not found",
                "No Product exists with the supplied identifier.",
                "catalog.product.not_found",
                productId),
            ProductPreparationConfigurationChangeOutcome.NotCurrent => Problem(
                StatusCodes.Status409Conflict,
                "Product is not current",
                "The Product exists but is not active.",
                "catalog.product.not_current",
                productId),
            ProductPreparationConfigurationChangeOutcome.ResponsibilityNotFound => Problem(
                StatusCodes.Status409Conflict,
                "Preparation Responsibility does not exist",
                "The requested Preparation Responsibility does not exist.",
                "catalog.product.preparation_responsibility_not_found",
                productId,
                preparationResponsibilityId:
                    result.InvalidPreparationResponsibilityId),
            ProductPreparationConfigurationChangeOutcome.ConcurrencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Product preparation configuration changed concurrently",
                "The current Product preparation configuration does not match the expected Responsibility.",
                "catalog.product.preparation_configuration_concurrency_conflict",
                productId,
                includeCurrentPreparationResponsibilityId: true,
                currentPreparationResponsibilityId:
                    result.CurrentPreparationResponsibilityId),
            ProductPreparationConfigurationChangeOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Product preparation configuration change.",
                "catalog.product.preparation_configuration.idempotency_key_conflict"),
            ProductPreparationConfigurationChangeOutcome.AuthenticationRequired =>
                AuthenticationRequired(),
            ProductPreparationConfigurationChangeOutcome.CatalogConfigurationRequired =>
                CatalogConfigurationRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> CreateGroupAsync(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        CreateGroupRequest request, HttpContext httpContext, IAntiforgery antiforgery,
        CatalogLifecycleService catalog, CancellationToken cancellationToken)
    {
        var command = await ValidateCatalogCommandAsync(idempotencyKey, httpContext, antiforgery, "catalog.group", cancellationToken);
        if (command.Failure is not null) return command.Failure;
        var result = await catalog.CreateGroupAsync(command.Key!.Value, request, cancellationToken);
        return result.Outcome switch
        {
            CatalogMutationOutcome.Created => Results.Created($"/api/catalog/groups/{result.Group!.Id}", result.Group),
            CatalogMutationOutcome.Invalid => Problem(StatusCodes.Status400BadRequest, "Invalid Group", result.Error!, "catalog.group.invalid", field: result.Field),
            CatalogMutationOutcome.NameConflict => Problem(StatusCodes.Status409Conflict, "Operational name already in use", "A Group already uses that operational name, ignoring case.", "catalog.group.operational_name_conflict"),
            CatalogMutationOutcome.IdempotencyConflict => IdempotencyConflict("catalog.group.idempotency_key_conflict"),
            CatalogMutationOutcome.AuthenticationRequired => AuthenticationRequired(),
            CatalogMutationOutcome.Forbidden => CatalogConfigurationRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ListGroupsAsync(CatalogLifecycleService catalog, CancellationToken cancellationToken)
    {
        var result = await catalog.ListGroupsAsync(cancellationToken);
        return result.Outcome switch
        {
            CatalogMutationOutcome.Changed => Results.Ok(result.Groups),
            CatalogMutationOutcome.AuthenticationRequired => AuthenticationRequired(),
            CatalogMutationOutcome.Forbidden => CatalogConfigurationRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ChangeProductGroupAsync(
        Guid productId, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        ChangeProductGroupRequest request, HttpContext httpContext, IAntiforgery antiforgery,
        CatalogLifecycleService catalog, CancellationToken cancellationToken)
    {
        var command = await ValidateCatalogCommandAsync(idempotencyKey, httpContext, antiforgery, "catalog.product.group_change", cancellationToken);
        if (command.Failure is not null) return command.Failure;
        var result = await catalog.ChangeGroupAsync(command.Key!.Value, productId, request, cancellationToken);
        return result.Outcome switch
        {
            CatalogMutationOutcome.Changed => Results.Ok(result.Result),
            CatalogMutationOutcome.GroupNotFound => Problem(StatusCodes.Status409Conflict, "Group not found", "The requested Group does not exist.", "catalog.group.not_found", productId, groupId: result.GroupId),
            CatalogMutationOutcome.NotFound => ProductNotFound(productId),
            CatalogMutationOutcome.NotCurrent => ProductNotCurrent(productId),
            CatalogMutationOutcome.Stale => Problem(StatusCodes.Status409Conflict, "Product Group changed concurrently", "The current Product Group does not match expectedCurrentGroupId.", "catalog.product.group_concurrency_conflict", productId, includeCurrentGroupId: true, currentGroupId: result.GroupId),
            CatalogMutationOutcome.IdempotencyConflict => IdempotencyConflict("catalog.product.group_change.idempotency_key_conflict"),
            CatalogMutationOutcome.AuthenticationRequired => AuthenticationRequired(),
            CatalogMutationOutcome.Forbidden => CatalogConfigurationRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ChangeProductOperationalNameAsync(
        Guid productId, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        ChangeProductOperationalNameRequest request, HttpContext httpContext, IAntiforgery antiforgery,
        CatalogLifecycleService catalog, CancellationToken cancellationToken)
    {
        var command = await ValidateCatalogCommandAsync(idempotencyKey, httpContext, antiforgery, "catalog.product.operational_name_change", cancellationToken);
        if (command.Failure is not null) return command.Failure;
        var result = await catalog.ChangeNameAsync(command.Key!.Value, productId, request, cancellationToken);
        return result.Outcome switch
        {
            CatalogMutationOutcome.Changed => Results.Ok(result.Result),
            CatalogMutationOutcome.Invalid => Problem(StatusCodes.Status400BadRequest, "Invalid Product name", result.Error!, "catalog.product.operational_name_invalid", productId, field: result.Field),
            CatalogMutationOutcome.NameConflict => Problem(StatusCodes.Status409Conflict, "Operational name already in use", "An active Product already uses that operational name, ignoring case.", "catalog.product.operational_name_conflict", productId),
            CatalogMutationOutcome.NotFound => ProductNotFound(productId),
            CatalogMutationOutcome.Stale => Problem(StatusCodes.Status409Conflict, "Product name changed concurrently", "The current Product name does not match expectedCurrentOperationalName.", "catalog.product.operational_name_concurrency_conflict", productId, currentOperationalName: result.CurrentName),
            CatalogMutationOutcome.IdempotencyConflict => IdempotencyConflict("catalog.product.operational_name_change.idempotency_key_conflict"),
            CatalogMutationOutcome.AuthenticationRequired => AuthenticationRequired(),
            CatalogMutationOutcome.Forbidden => CatalogConfigurationRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> RetireProductAsync(
        Guid productId, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext, IAntiforgery antiforgery, CatalogLifecycleService catalog,
        CancellationToken cancellationToken)
    {
        var command = await ValidateCatalogCommandAsync(idempotencyKey, httpContext, antiforgery, "catalog.product.retire", cancellationToken);
        if (command.Failure is not null) return command.Failure;
        var result = await catalog.RetireAsync(command.Key!.Value, productId, cancellationToken);
        return LifecycleResult(result, productId, "retire");
    }

    private static async Task<IResult> ReactivateProductAsync(
        Guid productId, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext, IAntiforgery antiforgery, CatalogLifecycleService catalog,
        CancellationToken cancellationToken)
    {
        var command = await ValidateCatalogCommandAsync(idempotencyKey, httpContext, antiforgery, "catalog.product.reactivate", cancellationToken);
        if (command.Failure is not null) return command.Failure;
        var result = await catalog.ReactivateAsync(command.Key!.Value, productId, cancellationToken);
        return LifecycleResult(result, productId, "reactivate");
    }

    private static async Task<IResult> DeleteProductAsync(
        Guid productId, [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        HttpContext httpContext, IAntiforgery antiforgery, ProductDeleteService catalog,
        CancellationToken cancellationToken)
    {
        var command = await ValidateCatalogCommandAsync(
            idempotencyKey, httpContext, antiforgery, "catalog.product.delete", cancellationToken);
        if (command.Failure is not null) return command.Failure;
        var result = await catalog.DeleteAsync(command.Key!.Value, productId, cancellationToken);
        return result.Outcome switch
        {
            ProductDeleteOutcome.Deleted => Results.Ok(new ProductDeleteResponse(productId)),
            ProductDeleteOutcome.NotFound => ProductNotFound(productId),
            ProductDeleteOutcome.ConfirmedParticipation => Problem(
                StatusCodes.Status409Conflict, "Product has confirmed Order history",
                "This Product participated in a confirmed Order and cannot be deleted. Retire it instead.",
                "catalog.product.delete.confirmed_participation", productId),
            ProductDeleteOutcome.IdempotencyConflict =>
                IdempotencyConflict("catalog.product.delete.idempotency_key_conflict"),
            ProductDeleteOutcome.AuthenticationRequired => AuthenticationRequired(),
            ProductDeleteOutcome.Forbidden => CatalogConfigurationRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static IResult LifecycleResult(ProductLifecycleCommandResult result, Guid productId, string command) => result.Outcome switch
    {
        CatalogMutationOutcome.Changed => Results.Ok(result.Result),
        CatalogMutationOutcome.NotFound => ProductNotFound(productId),
        CatalogMutationOutcome.AlreadyRetired => Problem(StatusCodes.Status409Conflict, "Product already retired", "The Product is already retired.", "catalog.product.already_retired", productId),
        CatalogMutationOutcome.AlreadyActive => Problem(StatusCodes.Status409Conflict, "Product already active", "The Product is already active.", "catalog.product.already_active", productId),
        CatalogMutationOutcome.NameConflict => Problem(StatusCodes.Status409Conflict, "Product name collision", "Another active Product uses this operational name.", "catalog.product.reactivation_name_conflict", productId),
        CatalogMutationOutcome.IdempotencyConflict => IdempotencyConflict($"catalog.product.{command}.idempotency_key_conflict"),
        CatalogMutationOutcome.AuthenticationRequired => AuthenticationRequired(),
        CatalogMutationOutcome.Forbidden => CatalogConfigurationRequired(),
        _ => throw new UnreachableException()
    };

    private static async Task<(Guid? Key, IResult? Failure)> ValidateCatalogCommandAsync(
        string? idempotencyKey, HttpContext context, IAntiforgery antiforgery, string codePrefix,
        CancellationToken token)
    {
        if (idempotencyKey is null) return (null, Problem(StatusCodes.Status400BadRequest, "Idempotency-Key is required", "This command requires an Idempotency-Key containing a UUID v4.", $"{codePrefix}.idempotency_key_required"));
        if (!Guid.TryParse(idempotencyKey, out var key) || !IsUuidVersion4(key)) return (null, Problem(StatusCodes.Status400BadRequest, "Invalid Idempotency-Key", "Idempotency-Key must contain a UUID v4.", $"{codePrefix}.idempotency_key_invalid"));
        var antiforgeryFailure = await ValidateAntiforgeryAsync(context, antiforgery);
        return antiforgeryFailure is null ? (key, null) : (null, antiforgeryFailure);
    }

    private static IResult ProductNotFound(Guid productId) => Problem(StatusCodes.Status404NotFound, "Product not found", "No Product exists with the supplied identifier.", "catalog.product.not_found", productId);
    private static IResult ProductNotCurrent(Guid productId) => Problem(StatusCodes.Status409Conflict, "Product is not current", "The Product exists but is not active.", "catalog.product.not_current", productId);
    private static IResult IdempotencyConflict(string code) => Problem(StatusCodes.Status409Conflict, "Idempotency-Key was already used for another intention", "The supplied Idempotency-Key identifies an incompatible command.", code);

    private static async Task<IResult> ChangeProductPriceAsync(
        Guid productId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        ChangeProductPriceRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        CatalogService catalog,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "Product price change requires an Idempotency-Key containing a UUID v4.",
                "catalog.product.idempotency_key_required");
        }

        if (!Guid.TryParse(idempotencyKey, out var commandId) || !IsUuidVersion4(commandId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key",
                "Idempotency-Key must contain a UUID v4.",
                "catalog.product.idempotency_key_invalid");
        }

        var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var result = await catalog.ChangeProductPriceAsync(
            commandId,
            productId,
            request,
            cancellationToken);

        return result.Outcome switch
        {
            ChangeProductPriceOutcome.Changed => Results.Ok(result.Product),
            ChangeProductPriceOutcome.Invalid => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid product price change intention",
                result.Error!,
                "catalog.product.price_change_invalid",
                field: result.InvalidField),
            ChangeProductPriceOutcome.NotFound => Problem(
                StatusCodes.Status404NotFound,
                "Product not found",
                "No Product exists with the supplied identifier.",
                "catalog.product.not_found",
                productId),
            ChangeProductPriceOutcome.NotCurrent => Problem(
                StatusCodes.Status409Conflict,
                "Product is not current",
                "The Product exists but is not active.",
                "catalog.product.not_current",
                productId),
            ChangeProductPriceOutcome.PriceConcurrencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Product price changed concurrently",
                "The current Product price does not match expectedCurrentPrice.",
                "catalog.product.price_concurrency_conflict",
                productId,
                currentPrice: result.CurrentPrice),
            ChangeProductPriceOutcome.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency-Key was already used for another intention",
                "The supplied Idempotency-Key identifies an incompatible Product price change.",
                "catalog.product.idempotency_key_conflict"),
            ChangeProductPriceOutcome.AuthenticationRequired => AuthenticationRequired(),
            ChangeProductPriceOutcome.CatalogConfigurationRequired =>
                CatalogConfigurationRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static IResult Problem(
        int statusCode,
        string title,
        string detail,
        string code,
        Guid? productId = null,
        string? field = null,
        string? currentPrice = null,
        Guid? preparationResponsibilityId = null,
        bool includeCurrentPreparationResponsibilityId = false,
        Guid? currentPreparationResponsibilityId = null,
        Guid? groupId = null,
        bool includeCurrentGroupId = false,
        Guid? currentGroupId = null,
        string? currentOperationalName = null,
        bool? currentAvailability = null)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code
        };

        if (productId is not null)
        {
            extensions["productId"] = productId;
        }

        if (field is not null)
        {
            extensions["field"] = field;
        }

        if (currentPrice is not null)
        {
            extensions["currentPrice"] = currentPrice;
        }

        if (preparationResponsibilityId is not null)
        {
            extensions["preparationResponsibilityId"] =
                preparationResponsibilityId;
        }

        if (includeCurrentPreparationResponsibilityId)
        {
            extensions["currentPreparationResponsibilityId"] =
                currentPreparationResponsibilityId;
        }

        if (groupId is not null)
        {
            extensions["groupId"] = groupId;
        }

        if (includeCurrentGroupId)
        {
            extensions["currentGroupId"] = currentGroupId;
        }

        if (currentOperationalName is not null)
        {
            extensions["currentOperationalName"] = currentOperationalName;
        }

        if (currentAvailability is not null)
        {
            extensions["currentAvailability"] = currentAvailability;
        }

        return Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: extensions);
    }

    private static async Task<IResult> CreateProductAsync(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        CreateProductRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        CatalogService catalog,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Idempotency-Key is required",
                detail: "Product creation requires an Idempotency-Key containing a UUID v4.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "catalog.product.idempotency_key_required"
                });
        }

        if (!Guid.TryParse(idempotencyKey, out var commandId) || !IsUuidVersion4(commandId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Idempotency-Key",
                detail: "Idempotency-Key must contain a UUID v4.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "catalog.product.idempotency_key_invalid"
                });
        }

        var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var result = await catalog.CreateProductAsync(
            commandId,
            request,
            cancellationToken);

        return result.Outcome switch
        {
            CreateProductOutcome.Created => Results.Created(
                $"/api/catalog/products/{result.Product!.Id}",
                result.Product),
            CreateProductOutcome.Invalid => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid product creation intention",
                detail: result.Error,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "catalog.product.invalid",
                    ["field"] = result.InvalidField
                }),
            CreateProductOutcome.DuplicateName => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Operational name already in use",
                detail: "An active product already uses that operational name, ignoring case.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "catalog.product.operational_name_conflict"
                }),
            CreateProductOutcome.IdempotencyConflict => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Idempotency-Key was already used for another intention",
                detail: "The supplied Idempotency-Key identifies a different product creation intention.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "catalog.product.idempotency_key_conflict"
                }),
            CreateProductOutcome.AuthenticationRequired => AuthenticationRequired(),
            CreateProductOutcome.CatalogConfigurationRequired =>
                CatalogConfigurationRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static bool IsUuidVersion4(Guid value)
    {
        var bytes = value.ToByteArray(bigEndian: true);
        var version = bytes[6] >> 4;
        var variant = bytes[8] & 0xc0;

        return version == 4 && variant == 0x80;
    }

    private static async Task<IResult> ListActiveProductsAsync(
        CatalogService catalog,
        CancellationToken cancellationToken)
    {
        var result = await catalog.ListAdministrativeProductsAsync(cancellationToken);
        return result.Outcome switch
        {
            CatalogAccessOutcome.Succeeded => Results.Ok(result.Products),
            CatalogAccessOutcome.AuthenticationRequired => AuthenticationRequired(),
            CatalogAccessOutcome.CatalogConfigurationRequired =>
                CatalogConfigurationRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> FindActiveProductAsync(
        Guid id,
        CatalogService catalog,
        CancellationToken cancellationToken)
    {
        var result = await catalog.FindAdministrativeProductAsync(id, cancellationToken);

        return result.Outcome switch
        {
            CatalogProductOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Product not found",
                detail: "No Product exists with the supplied identifier.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "catalog.product.not_found"
                }),
            CatalogProductOutcome.Succeeded => Results.Ok(result.Product),
            CatalogProductOutcome.AuthenticationRequired => AuthenticationRequired(),
            CatalogProductOutcome.CatalogConfigurationRequired =>
                CatalogConfigurationRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ListOperationalProductsAsync(
        CatalogService catalog,
        CancellationToken cancellationToken)
    {
        var result = await catalog.ListOperationalProductsAsync(cancellationToken);
        return result.Outcome switch
        {
            CatalogAccessOutcome.Succeeded => Results.Ok(result.Products),
            CatalogAccessOutcome.AuthenticationRequired => AuthenticationRequired(),
            CatalogAccessOutcome.OrderOperationsRequired => Problem(
                StatusCodes.Status403Forbidden,
                "Order Operations required",
                "A current Order Operations and Basic Closure responsibility is required.",
                "identities_and_capabilities.order_operations.forbidden"),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ListAvailabilityAdministrationProductsAsync(
        CatalogService catalog,
        CancellationToken cancellationToken)
    {
        var result = await catalog.ListAvailabilityAdministrationProductsAsync(
            cancellationToken);
        return result.Outcome switch
        {
            CatalogAccessOutcome.Succeeded => Results.Ok(result.Products),
            CatalogAccessOutcome.AuthenticationRequired => AuthenticationRequired(),
            CatalogAccessOutcome.OperationalInterventionRequired =>
                OperationalInterventionRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ChangeProductAvailabilityAsync(
        Guid productId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        ChangeProductAvailabilityRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        CatalogService catalog,
        CancellationToken cancellationToken)
    {
        var command = await ValidateCatalogCommandAsync(
            idempotencyKey,
            httpContext,
            antiforgery,
            "catalog.product.availability",
            cancellationToken);
        if (command.Failure is not null)
        {
            return command.Failure;
        }

        var result = await catalog.ChangeProductAvailabilityAsync(
            command.Key!.Value,
            productId,
            request,
            cancellationToken);
        return result.Outcome switch
        {
            ProductAvailabilityChangeOutcome.Changed => Results.Ok(result.Product),
            ProductAvailabilityChangeOutcome.Invalid => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Product availability change intention",
                "Both expectedCurrentAvailability and newAvailability are required.",
                "catalog.product.availability.invalid"),
            ProductAvailabilityChangeOutcome.NotFound => ProductNotFound(productId),
            ProductAvailabilityChangeOutcome.NotCurrent => ProductNotCurrent(productId),
            ProductAvailabilityChangeOutcome.ConcurrencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Product availability changed concurrently",
                "The current Product availability does not match expectedCurrentAvailability.",
                "catalog.product.availability_concurrency_conflict",
                productId,
                currentAvailability: result.CurrentAvailability),
            ProductAvailabilityChangeOutcome.IdempotencyConflict => IdempotencyConflict(
                "catalog.product.availability.idempotency_key_conflict"),
            ProductAvailabilityChangeOutcome.AuthenticationRequired => AuthenticationRequired(),
            ProductAvailabilityChangeOutcome.Forbidden => OperationalInterventionRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult> ListPreparationResponsibilitiesAsync(
        CatalogService catalog,
        CancellationToken cancellationToken)
    {
        var result = await catalog.ListPreparationResponsibilitiesAsync(cancellationToken);
        return result.Outcome switch
        {
            CatalogAccessOutcome.Succeeded => Results.Ok(result.Responsibilities),
            CatalogAccessOutcome.AuthenticationRequired => AuthenticationRequired(),
            CatalogAccessOutcome.CatalogConfigurationRequired =>
                CatalogConfigurationRequired(),
            _ => throw new UnreachableException()
        };
    }

    private static async Task<IResult?> ValidateAntiforgeryAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery)
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
                "identities_and_capabilities.antiforgery_invalid");
        }
    }

    private static IResult AuthenticationRequired() => Problem(
        StatusCodes.Status401Unauthorized,
        "Invalid session",
        "The current session is invalid or expired.",
        "identities_and_capabilities.invalid_session");

    private static IResult CatalogConfigurationRequired() => Problem(
        StatusCodes.Status403Forbidden,
        "Catalog Configuration required",
        "A current Catalog Configuration responsibility is required.",
        "identities_and_capabilities.catalog_configuration_required");

    private static IResult OperationalInterventionRequired() => Problem(
        StatusCodes.Status403Forbidden,
        "Operational Intervention required",
        "A current Operational Intervention responsibility is required.",
        "identities_and_capabilities.operational_intervention_required");
}
