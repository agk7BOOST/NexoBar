using System.Diagnostics;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        return services;
    }

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/catalog/products")
            .WithTags("Catalog");

        group.MapPost(string.Empty, CreateProductAsync)
            .WithName("CreateCatalogProduct")
            .Accepts<CreateProductRequest>("application/json")
            .Produces<ProductResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet(string.Empty, ListActiveProductsAsync)
            .WithName("ListActiveCatalogProducts")
            .Produces<IReadOnlyList<ProductResponse>>();

        group.MapGet("/{id:guid}", FindActiveProductAsync)
            .WithName("GetActiveCatalogProduct")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> CreateProductAsync(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        CreateProductRequest request,
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
        CancellationToken cancellationToken) =>
        Results.Ok(await catalog.ListActiveProductsAsync(cancellationToken));

    private static async Task<IResult> FindActiveProductAsync(
        Guid id,
        CatalogService catalog,
        CancellationToken cancellationToken)
    {
        var product = await catalog.FindActiveProductAsync(id, cancellationToken);

        return product is null
            ? Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Product not found",
                detail: "No active product exists with the supplied identifier.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "catalog.product.not_found"
                })
            : Results.Ok(product);
    }
}
