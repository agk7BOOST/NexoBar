using System.Buffers.Binary;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NexoBar.Catalog;

internal sealed class CatalogService(CatalogDbContext dbContext)
{
    private const long ProductCreationLockNamespace = 0x434154414C4F4700;

    internal async Task<CreateProductResult> CreateProductAsync(
        Guid idempotencyKey,
        CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        var validation = Validate(request);
        if (validation.Error is not null)
        {
            return validation.Error;
        }

        var intent = validation.Intent!;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);

        var lockKey = CreateTransactionLockKey(idempotencyKey);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);

        var existingCommand = await dbContext.ProductCreationCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);

        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);

            return existingCommand.Matches(
                intent.OperationalName,
                intent.Price,
                intent.RequiresPreparation)
                ? CreateProductResult.Created(Map(existingCommand))
                : CreateProductResult.IdempotencyConflict();
        }

        var product = new Product(
            Guid.CreateVersion7(),
            intent.OperationalName,
            intent.Price);
        var response = Map(product);
        var command = new ProductCreationCommand(
            idempotencyKey,
            intent.OperationalName,
            intent.Price,
            intent.RequiresPreparation,
            response);

        dbContext.Products.Add(product);
        dbContext.ProductCreationCommands.Add(command);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "UX_catalog_products_active_normalized_operational_name"
            })
        {
            await transaction.RollbackAsync(cancellationToken);
            return CreateProductResult.DuplicateName();
        }

        return CreateProductResult.Created(response);
    }

    internal async Task<IReadOnlyList<ProductResponse>> ListActiveProductsAsync(
        CancellationToken cancellationToken)
    {
        var products = await dbContext.Products
            .AsNoTracking()
            .Where(product => product.IsActive)
            .OrderBy(product => product.OperationalName)
            .ThenBy(product => product.Id)
            .ToListAsync(cancellationToken);

        return products.Select(Map).ToArray();
    }

    internal async Task<ProductResponse?> FindActiveProductAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var product = await dbContext.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == id && candidate.IsActive,
                cancellationToken);

        return product is null ? null : Map(product);
    }

    private static ProductValidation Validate(CreateProductRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OperationalName))
        {
            return ProductValidation.Invalid(
                CreateProductResult.Invalid(
                    "operationalName",
                    "An operational name is required."));
        }

        if (request.RequiresPreparation is not false)
        {
            return ProductValidation.Invalid(
                CreateProductResult.Invalid(
                    "requiresPreparation",
                    "requiresPreparation must be explicitly false in increment I1."));
        }

        const NumberStyles allowedPriceStyles =
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

        if (request.Price is null ||
            !decimal.TryParse(
                request.Price,
                allowedPriceStyles,
                CultureInfo.InvariantCulture,
                out var price))
        {
            return ProductValidation.Invalid(
                CreateProductResult.Invalid(
                    "price",
                    "Price must be a decimal string using '.' as the decimal separator."));
        }

        if (price < 0)
        {
            return ProductValidation.Invalid(
                CreateProductResult.Invalid(
                    "price",
                    "Price must be greater than or equal to zero."));
        }

        return ProductValidation.Valid(
            new ValidatedCreateProductIntent(
                request.OperationalName,
                price,
                RequiresPreparation: false));
    }

    private static long CreateTransactionLockKey(Guid idempotencyKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);

        return ProductCreationLockNamespace ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }

    private static ProductResponse Map(Product product) =>
        new(
            product.Id,
            product.OperationalName,
            product.Price.ToString(CultureInfo.InvariantCulture),
            product.IsActive,
            product.IsAvailable,
            product.RequiresPreparation);

    private static ProductResponse Map(ProductCreationCommand command) =>
        new(
            command.ResultProductId,
            command.IntentOperationalName,
            command.IntentPrice.ToString(CultureInfo.InvariantCulture),
            command.ResultIsActive,
            command.ResultIsAvailable,
            command.IntentRequiresPreparation);
}

internal sealed record ProductValidation(
    ValidatedCreateProductIntent? Intent,
    CreateProductResult? Error)
{
    internal static ProductValidation Valid(ValidatedCreateProductIntent intent) =>
        new(intent, null);

    internal static ProductValidation Invalid(CreateProductResult error) =>
        new(null, error);
}

internal sealed record ValidatedCreateProductIntent(
    string OperationalName,
    decimal Price,
    bool RequiresPreparation);

internal sealed record CreateProductResult(
    CreateProductOutcome Outcome,
    ProductResponse? Product,
    string? InvalidField,
    string? Error)
{
    internal static CreateProductResult Created(ProductResponse product) =>
        new(CreateProductOutcome.Created, product, null, null);

    internal static CreateProductResult DuplicateName() =>
        new(CreateProductOutcome.DuplicateName, null, null, null);

    internal static CreateProductResult IdempotencyConflict() =>
        new(CreateProductOutcome.IdempotencyConflict, null, null, null);

    internal static CreateProductResult Invalid(string field, string error) =>
        new(CreateProductOutcome.Invalid, null, field, error);
}

internal enum CreateProductOutcome
{
    Created,
    Invalid,
    DuplicateName,
    IdempotencyConflict
}
