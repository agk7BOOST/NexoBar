using System.Buffers.Binary;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OperationalConfiguration;
using Npgsql;

namespace NexoBar.Catalog;

internal sealed class CatalogService(
    CatalogDbContext dbContext,
    IPreparationResponsibilityLookup preparationResponsibilities,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    ICatalogConfigurationCapabilityStabilizer catalogConfiguration,
    IOrderOperationsAuthorization orderOperationsAuthorization,
    IOperationalInterventionCapabilityStabilizer operationalIntervention)
{
    private const long ProductCreationLockNamespace = 0x434154414C4F4700;
    private const long ProductPriceChangeLockNamespace = 0x5052494345434800;
    private const long ProductPreparationConfigurationChangeLockNamespace =
        0x5052455043464700;

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

        var actor = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(), cancellationToken);
        if (actor is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return CreateProductResult.AuthenticationRequired();
        }

        var existingCommand = await dbContext.ProductCreationCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);

        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);

            return existingCommand.Matches(
                actor.IdentityId,
                intent.OperationalName,
                intent.Price,
                intent.RequiresPreparation)
                ? CreateProductResult.Created(Map(existingCommand))
                : CreateProductResult.IdempotencyConflict();
        }

        if (!await catalogConfiguration.StabilizeResponsibilityAsync(
                actor.IdentityId, transaction.GetDbTransaction(), cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return CreateProductResult.CatalogConfigurationRequired();
        }

        var product = new Product(
            Guid.CreateVersion7(),
            intent.OperationalName,
            intent.Price);
        var response = Map(product);
        var command = new ProductCreationCommand(
            idempotencyKey,
            actor.IdentityId,
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

    internal async Task<CatalogProductListResult> ListAdministrativeProductsAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var actor = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(), cancellationToken);
        if (actor is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return CatalogProductListResult.AuthenticationRequired();
        }
        if (!await catalogConfiguration.StabilizeResponsibilityAsync(
                actor.IdentityId, transaction.GetDbTransaction(), cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return CatalogProductListResult.CatalogConfigurationRequired();
        }
        var products = await dbContext.Products
            .AsNoTracking()
            .Where(product => product.IsActive)
            .OrderBy(product => product.OperationalName)
            .ThenBy(product => product.Id)
            .ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return CatalogProductListResult.Succeeded(products.Select(Map).ToArray());
    }

    internal async Task<ChangeProductPriceResult> ChangeProductPriceAsync(
        Guid idempotencyKey,
        Guid productId,
        ChangeProductPriceRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ValidatePriceChange(request);
        if (validation.Error is not null)
        {
            return validation.Error;
        }

        var intent = validation.Intent!;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);

        var lockKey = CreateTransactionLockKey(
            idempotencyKey,
            ProductPriceChangeLockNamespace);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);

        var actor = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(), cancellationToken);
        if (actor is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ChangeProductPriceResult.AuthenticationRequired();
        }

        var existingCommand = await dbContext.ProductPriceChangeCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);

        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);

            return existingCommand.Matches(
                actor.IdentityId,
                productId,
                intent.ExpectedCurrentPrice,
                intent.NewPrice)
                ? ChangeProductPriceResult.Changed(Map(existingCommand))
                : ChangeProductPriceResult.IdempotencyConflict();
        }

        if (!await catalogConfiguration.StabilizeResponsibilityAsync(
                actor.IdentityId, transaction.GetDbTransaction(), cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ChangeProductPriceResult.CatalogConfigurationRequired();
        }

        var affectedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE catalog.products
            SET price = {intent.NewPrice}
            WHERE id = {productId}
              AND is_active
              AND price = {intent.ExpectedCurrentPrice}
            """,
            cancellationToken);

        if (affectedRows == 0)
        {
            var product = await dbContext.Database
                .SqlQuery<PriceChangeDiagnostic>(
                    $"""
                    SELECT is_active AS "IsActive",
                           price AS "Price"
                    FROM catalog.products
                    WHERE id = {productId}
                    FOR UPDATE
                    """)
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            if (product is null)
            {
                return ChangeProductPriceResult.NotFound();
            }

            if (!product.IsActive)
            {
                return ChangeProductPriceResult.NotCurrent();
            }

            return ChangeProductPriceResult.PriceConcurrencyConflict(
                product.Price.ToString(CultureInfo.InvariantCulture));
        }

        dbContext.ProductPriceChangeCommands.Add(new ProductPriceChangeCommand(
            idempotencyKey,
            actor.IdentityId,
            productId,
            intent.ExpectedCurrentPrice,
            intent.NewPrice));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ChangeProductPriceResult.Changed(
            new ProductPriceResponse(
                productId,
                intent.NewPrice.ToString(CultureInfo.InvariantCulture)));
    }

    internal async Task<CatalogProductResult> FindAdministrativeProductAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var actor = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(), cancellationToken);
        if (actor is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return CatalogProductResult.AuthenticationRequired();
        }
        if (!await catalogConfiguration.StabilizeResponsibilityAsync(
                actor.IdentityId, transaction.GetDbTransaction(), cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return CatalogProductResult.CatalogConfigurationRequired();
        }
        var product = await dbContext.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == id && candidate.IsActive,
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return product is null
            ? CatalogProductResult.NotFound()
            : CatalogProductResult.Succeeded(Map(product));
    }

    internal async Task<ProductPreparationConfigurationChangeResult>
        ChangeProductPreparationConfigurationAsync(
            Guid idempotencyKey,
            Guid productId,
            ChangeProductPreparationConfigurationRequest request,
            CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);

        var lockKey = CreateTransactionLockKey(
            idempotencyKey,
            ProductPreparationConfigurationChangeLockNamespace);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);

        var actor = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(), cancellationToken);
        if (actor is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ProductPreparationConfigurationChangeResult.AuthenticationRequired();
        }

        var existingCommand = await dbContext
            .ProductPreparationConfigurationChangeCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);

        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existingCommand.Matches(
                actor.IdentityId,
                productId,
                request.ExpectedCurrentPreparationResponsibilityId,
                request.NewPreparationResponsibilityId)
                ? ProductPreparationConfigurationChangeResult.Changed(
                    Map(existingCommand))
                : ProductPreparationConfigurationChangeResult.IdempotencyConflict();
        }

        if (!await catalogConfiguration.StabilizeResponsibilityAsync(
                actor.IdentityId, transaction.GetDbTransaction(), cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ProductPreparationConfigurationChangeResult.CatalogConfigurationRequired();
        }

        if (request.NewPreparationResponsibilityId is Guid responsibilityId &&
            !await preparationResponsibilities.ExistsAsync(
                responsibilityId,
                cancellationToken))
        {
            return ProductPreparationConfigurationChangeResult
                .ResponsibilityNotFound(responsibilityId);
        }

        var requiresPreparation = request.NewPreparationResponsibilityId is not null;
        var affectedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE catalog.products
            SET requires_preparation = {requiresPreparation},
                preparation_responsibility_id = {request.NewPreparationResponsibilityId}
            WHERE id = {productId}
              AND is_active
              AND preparation_responsibility_id IS NOT DISTINCT FROM
                  {request.ExpectedCurrentPreparationResponsibilityId}
            """,
            cancellationToken);

        if (affectedRows == 0)
        {
            var product = await dbContext.Database
                .SqlQuery<PreparationConfigurationDiagnostic>(
                    $"""
                    SELECT is_active AS "IsActive",
                           preparation_responsibility_id AS
                               "PreparationResponsibilityId"
                    FROM catalog.products
                    WHERE id = {productId}
                    FOR UPDATE
                    """)
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            if (product is null)
            {
                return ProductPreparationConfigurationChangeResult.NotFound();
            }
            if (!product.IsActive)
            {
                return ProductPreparationConfigurationChangeResult.NotCurrent();
            }
            return ProductPreparationConfigurationChangeResult.ConcurrencyConflict(
                product.PreparationResponsibilityId);
        }

        var command = new ProductPreparationConfigurationChangeCommand(
            idempotencyKey,
            actor.IdentityId,
            productId,
            request.ExpectedCurrentPreparationResponsibilityId,
            request.NewPreparationResponsibilityId);
        dbContext.ProductPreparationConfigurationChangeCommands.Add(command);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProductPreparationConfigurationChangeResult.Changed(Map(command));
    }

    internal async Task<OperationalProductListResult> ListOperationalProductsAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var authorization = await orderOperationsAuthorization.AuthorizeAsync(
            transaction.GetDbTransaction(), cancellationToken);
        if (authorization != OrderOperationsAuthorizationOutcome.Authorized)
        {
            await transaction.RollbackAsync(cancellationToken);
            return authorization == OrderOperationsAuthorizationOutcome.Unauthenticated
                ? OperationalProductListResult.AuthenticationRequired()
                : OperationalProductListResult.OrderOperationsRequired();
        }

        var session = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(), cancellationToken)
            ?? throw new InvalidOperationException(
                "An authorized operational Catalog read lost its stabilized session.");
        var canReadUnavailable = await operationalIntervention
            .StabilizeResponsibilityAsync(
                session.IdentityId, transaction.GetDbTransaction(), cancellationToken);
        var products = await dbContext.Products.AsNoTracking()
            .Where(product => product.IsActive &&
                (canReadUnavailable || product.IsAvailable))
            .OrderBy(product => product.OperationalName)
            .ThenBy(product => product.Id)
            .Select(product => new OperationalProductResponse(
                product.Id,
                product.OperationalName,
                product.Price.ToString(CultureInfo.InvariantCulture),
                product.IsAvailable))
            .ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationalProductListResult.Succeeded(products);
    }

    internal async Task<PreparationResponsibilityListResult>
        ListPreparationResponsibilitiesAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var actor = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(), cancellationToken);
        if (actor is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PreparationResponsibilityListResult.AuthenticationRequired();
        }
        if (!await catalogConfiguration.StabilizeResponsibilityAsync(
                actor.IdentityId, transaction.GetDbTransaction(), cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return PreparationResponsibilityListResult.CatalogConfigurationRequired();
        }
        var responsibilities = await preparationResponsibilities.ListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PreparationResponsibilityListResult.Succeeded(responsibilities);
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

    private static PriceChangeValidation ValidatePriceChange(ChangeProductPriceRequest request)
    {
        if (!TryParsePrice(request.ExpectedCurrentPrice, out var expectedCurrentPrice))
        {
            return PriceChangeValidation.Invalid(ChangeProductPriceResult.Invalid(
                "expectedCurrentPrice",
                "expectedCurrentPrice must be a decimal string using '.' as the decimal separator."));
        }

        if (!TryParsePrice(request.NewPrice, out var newPrice))
        {
            return PriceChangeValidation.Invalid(ChangeProductPriceResult.Invalid(
                "newPrice",
                "newPrice must be a decimal string using '.' as the decimal separator."));
        }

        if (newPrice < 0)
        {
            return PriceChangeValidation.Invalid(ChangeProductPriceResult.Invalid(
                "newPrice",
                "newPrice must be greater than or equal to zero."));
        }

        return PriceChangeValidation.Valid(
            new ValidatedPriceChangeIntent(expectedCurrentPrice, newPrice));
    }

    private static bool TryParsePrice(string? value, out decimal price)
    {
        const NumberStyles allowedPriceStyles =
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

        price = default;
        return value is not null && decimal.TryParse(
            value,
            allowedPriceStyles,
            CultureInfo.InvariantCulture,
            out price);
    }

    private static long CreateTransactionLockKey(Guid idempotencyKey)
        => CreateTransactionLockKey(idempotencyKey, ProductCreationLockNamespace);

    private static long CreateTransactionLockKey(Guid idempotencyKey, long lockNamespace)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);

        return lockNamespace ^
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
            product.RequiresPreparation,
            product.PreparationResponsibilityId);

    private static ProductResponse Map(ProductCreationCommand command) =>
        new(
            command.ResultProductId,
            command.IntentOperationalName,
            command.IntentPrice.ToString(CultureInfo.InvariantCulture),
            command.ResultIsActive,
            command.ResultIsAvailable,
            command.IntentRequiresPreparation,
            PreparationResponsibilityId: null);

    private static ProductPriceResponse Map(ProductPriceChangeCommand command) =>
        new(
            command.ProductId,
            command.ResultPrice.ToString(CultureInfo.InvariantCulture));

    private static ProductPreparationConfigurationResponse Map(
        ProductPreparationConfigurationChangeCommand command) =>
        new(
            command.ProductId,
            command.ResultResponsibilityId is not null,
            command.ResultResponsibilityId);
}

internal sealed record PreparationConfigurationDiagnostic(
    bool IsActive,
    Guid? PreparationResponsibilityId);

internal sealed record ProductPreparationConfigurationChangeResult(
    ProductPreparationConfigurationChangeOutcome Outcome,
    ProductPreparationConfigurationResponse? Configuration,
    Guid? CurrentPreparationResponsibilityId,
    Guid? InvalidPreparationResponsibilityId)
{
    internal static ProductPreparationConfigurationChangeResult Changed(
        ProductPreparationConfigurationResponse configuration) =>
        new(
            ProductPreparationConfigurationChangeOutcome.Changed,
            configuration,
            null,
            null);
    internal static ProductPreparationConfigurationChangeResult NotFound() =>
        new(ProductPreparationConfigurationChangeOutcome.NotFound, null, null, null);
    internal static ProductPreparationConfigurationChangeResult NotCurrent() =>
        new(ProductPreparationConfigurationChangeOutcome.NotCurrent, null, null, null);
    internal static ProductPreparationConfigurationChangeResult ResponsibilityNotFound(
        Guid responsibilityId) =>
        new(
            ProductPreparationConfigurationChangeOutcome.ResponsibilityNotFound,
            null,
            null,
            responsibilityId);
    internal static ProductPreparationConfigurationChangeResult ConcurrencyConflict(
        Guid? currentResponsibilityId) =>
        new(
            ProductPreparationConfigurationChangeOutcome.ConcurrencyConflict,
            null,
            currentResponsibilityId,
            null);
    internal static ProductPreparationConfigurationChangeResult IdempotencyConflict() =>
        new(
            ProductPreparationConfigurationChangeOutcome.IdempotencyConflict,
            null,
            null,
            null);
    internal static ProductPreparationConfigurationChangeResult AuthenticationRequired() =>
        new(ProductPreparationConfigurationChangeOutcome.AuthenticationRequired, null, null, null);
    internal static ProductPreparationConfigurationChangeResult CatalogConfigurationRequired() =>
        new(ProductPreparationConfigurationChangeOutcome.CatalogConfigurationRequired, null, null, null);
}

internal enum ProductPreparationConfigurationChangeOutcome
{
    Changed,
    NotFound,
    NotCurrent,
    ResponsibilityNotFound,
    ConcurrencyConflict,
    IdempotencyConflict,
    AuthenticationRequired,
    CatalogConfigurationRequired
}

internal sealed record PriceChangeValidation(
    ValidatedPriceChangeIntent? Intent,
    ChangeProductPriceResult? Error)
{
    internal static PriceChangeValidation Valid(ValidatedPriceChangeIntent intent) =>
        new(intent, null);

    internal static PriceChangeValidation Invalid(ChangeProductPriceResult error) =>
        new(null, error);
}

internal sealed record ValidatedPriceChangeIntent(
    decimal ExpectedCurrentPrice,
    decimal NewPrice);

internal sealed record PriceChangeDiagnostic(bool IsActive, decimal Price);

internal sealed record ChangeProductPriceResult(
    ChangeProductPriceOutcome Outcome,
    ProductPriceResponse? Product,
    string? InvalidField,
    string? Error,
    string? CurrentPrice)
{
    internal static ChangeProductPriceResult Changed(ProductPriceResponse product) =>
        new(ChangeProductPriceOutcome.Changed, product, null, null, null);

    internal static ChangeProductPriceResult Invalid(string field, string error) =>
        new(ChangeProductPriceOutcome.Invalid, null, field, error, null);

    internal static ChangeProductPriceResult NotFound() =>
        new(ChangeProductPriceOutcome.NotFound, null, null, null, null);

    internal static ChangeProductPriceResult NotCurrent() =>
        new(ChangeProductPriceOutcome.NotCurrent, null, null, null, null);

    internal static ChangeProductPriceResult PriceConcurrencyConflict(string currentPrice) =>
        new(ChangeProductPriceOutcome.PriceConcurrencyConflict, null, null, null, currentPrice);

    internal static ChangeProductPriceResult IdempotencyConflict() =>
        new(ChangeProductPriceOutcome.IdempotencyConflict, null, null, null, null);
    internal static ChangeProductPriceResult AuthenticationRequired() =>
        new(ChangeProductPriceOutcome.AuthenticationRequired, null, null, null, null);
    internal static ChangeProductPriceResult CatalogConfigurationRequired() =>
        new(ChangeProductPriceOutcome.CatalogConfigurationRequired, null, null, null, null);
}

internal enum ChangeProductPriceOutcome
{
    Changed,
    Invalid,
    NotFound,
    NotCurrent,
    PriceConcurrencyConflict,
    IdempotencyConflict,
    AuthenticationRequired,
    CatalogConfigurationRequired
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
    internal static CreateProductResult AuthenticationRequired() =>
        new(CreateProductOutcome.AuthenticationRequired, null, null, null);
    internal static CreateProductResult CatalogConfigurationRequired() =>
        new(CreateProductOutcome.CatalogConfigurationRequired, null, null, null);
}

internal enum CreateProductOutcome
{
    Created,
    Invalid,
    DuplicateName,
    IdempotencyConflict,
    AuthenticationRequired,
    CatalogConfigurationRequired
}

internal sealed record CatalogProductListResult(
    CatalogAccessOutcome Outcome,
    IReadOnlyList<ProductResponse>? Products)
{
    internal static CatalogProductListResult Succeeded(IReadOnlyList<ProductResponse> products) =>
        new(CatalogAccessOutcome.Succeeded, products);
    internal static CatalogProductListResult AuthenticationRequired() =>
        new(CatalogAccessOutcome.AuthenticationRequired, null);
    internal static CatalogProductListResult CatalogConfigurationRequired() =>
        new(CatalogAccessOutcome.CatalogConfigurationRequired, null);
}

internal sealed record CatalogProductResult(
    CatalogProductOutcome Outcome,
    ProductResponse? Product)
{
    internal static CatalogProductResult Succeeded(ProductResponse product) =>
        new(CatalogProductOutcome.Succeeded, product);
    internal static CatalogProductResult NotFound() =>
        new(CatalogProductOutcome.NotFound, null);
    internal static CatalogProductResult AuthenticationRequired() =>
        new(CatalogProductOutcome.AuthenticationRequired, null);
    internal static CatalogProductResult CatalogConfigurationRequired() =>
        new(CatalogProductOutcome.CatalogConfigurationRequired, null);
}

internal sealed record OperationalProductListResult(
    CatalogAccessOutcome Outcome,
    IReadOnlyList<OperationalProductResponse>? Products)
{
    internal static OperationalProductListResult Succeeded(
        IReadOnlyList<OperationalProductResponse> products) =>
        new(CatalogAccessOutcome.Succeeded, products);
    internal static OperationalProductListResult AuthenticationRequired() =>
        new(CatalogAccessOutcome.AuthenticationRequired, null);
    internal static OperationalProductListResult OrderOperationsRequired() =>
        new(CatalogAccessOutcome.OrderOperationsRequired, null);
}

internal sealed record PreparationResponsibilityListResult(
    CatalogAccessOutcome Outcome,
    IReadOnlyList<PreparationResponsibilityReference>? Responsibilities)
{
    internal static PreparationResponsibilityListResult Succeeded(
        IReadOnlyList<PreparationResponsibilityReference> responsibilities) =>
        new(CatalogAccessOutcome.Succeeded, responsibilities);
    internal static PreparationResponsibilityListResult AuthenticationRequired() =>
        new(CatalogAccessOutcome.AuthenticationRequired, null);
    internal static PreparationResponsibilityListResult CatalogConfigurationRequired() =>
        new(CatalogAccessOutcome.CatalogConfigurationRequired, null);
}

internal enum CatalogAccessOutcome
{
    Succeeded,
    AuthenticationRequired,
    CatalogConfigurationRequired,
    OrderOperationsRequired
}

internal enum CatalogProductOutcome
{
    Succeeded,
    NotFound,
    AuthenticationRequired,
    CatalogConfigurationRequired
}
