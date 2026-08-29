using System.Buffers.Binary;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.Catalog;

namespace NexoBar.OrderOperations;

internal sealed class FirstConfirmationService(
    OrderOperationsDbContext dbContext,
    IOrderConfirmationCatalog catalog)
{
    private const long FirstConfirmationLockNamespace = 0x4F524445524F5000;

    internal async Task<FirstConfirmationResult> ConfirmAsync(
        Guid idempotencyKey,
        FirstConfirmationRequest request,
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

        var existingCommand = await dbContext.FirstConfirmationCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);

        if (existingCommand is not null)
        {
            var existingContents = await dbContext.FirstConfirmationCommandContents
                .AsNoTracking()
                .Where(content => content.IdempotencyKey == idempotencyKey)
                .OrderBy(content => content.ProductId)
                .ToArrayAsync(cancellationToken);

            if (!Matches(existingCommand, existingContents, intent))
            {
                await transaction.CommitAsync(cancellationToken);
                return FirstConfirmationResult.IdempotencyConflict();
            }

            var replay = await LoadOriginalResponseAsync(
                existingCommand.ResultIncorporationId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return FirstConfirmationResult.Confirmed(replay);
        }

        var catalogProducts = await catalog.ReadProductsAsync(
            intent.Items.Select(item => item.ProductId).ToArray(),
            transaction.GetDbTransaction(),
            cancellationToken);
        var productsById = catalogProducts.ToDictionary(product => product.ProductId);

        foreach (var item in intent.Items)
        {
            if (!productsById.TryGetValue(item.ProductId, out var product) ||
                !product.IsActive)
            {
                return FirstConfirmationResult.ProductNotCurrent(item.ProductId);
            }

            if (!product.IsAvailable)
            {
                return FirstConfirmationResult.ProductUnavailable(item.ProductId);
            }

            if (product.RequiresPreparation)
            {
                return FirstConfirmationResult.RequiresPreparationNotSupported(item.ProductId);
            }
        }

        var orderId = Guid.CreateVersion7();
        var incorporationId = Guid.CreateVersion7();
        var utcNow = DateTimeOffset.UtcNow;
        var confirmedAt = new DateTimeOffset(
            utcNow.Ticks - (utcNow.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);

        dbContext.Orders.Add(new Order(orderId, intent.Context));
        dbContext.Incorporations.Add(new Incorporation(incorporationId, orderId, ordinal: 1));
        dbContext.ConfirmationHistory.Add(new ConfirmationHistory(
            Guid.CreateVersion7(),
            incorporationId,
            intent.Context,
            confirmedAt));
        dbContext.FirstConfirmationCommands.Add(new FirstConfirmationCommand(
            idempotencyKey,
            intent.Context,
            incorporationId));

        var responseItems = new List<ConfirmedItemResponse>(intent.Items.Count);
        foreach (var item in intent.Items)
        {
            var appliedPrice = productsById[item.ProductId].Price;
            dbContext.IncorporationContents.Add(new IncorporationContent(
                incorporationId,
                item.ProductId,
                item.Quantity,
                appliedPrice));
            dbContext.FirstConfirmationCommandContents.Add(
                new FirstConfirmationCommandContent(
                    idempotencyKey,
                    item.ProductId,
                    item.Quantity));
            responseItems.Add(new ConfirmedItemResponse(
                item.ProductId,
                item.Quantity,
                appliedPrice.ToString(CultureInfo.InvariantCulture)));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return FirstConfirmationResult.Confirmed(
            new FirstConfirmationResponse(
                orderId.ToString("D"),
                intent.Context,
                new FirstIncorporationResponse(
                    incorporationId,
                    confirmedAt,
                    responseItems)));
    }

    private async Task<FirstConfirmationResponse> LoadOriginalResponseAsync(
        Guid incorporationId,
        CancellationToken cancellationToken)
    {
        var header = await (
            from incorporation in dbContext.Incorporations.AsNoTracking()
            join order in dbContext.Orders.AsNoTracking()
                on incorporation.OrderId equals order.Id
            join history in dbContext.ConfirmationHistory.AsNoTracking()
                on incorporation.Id equals history.IncorporationId
            where incorporation.Id == incorporationId
            select new
            {
                OrderId = order.Id,
                IncorporationId = incorporation.Id,
                history.ConfirmedContext,
                history.OccurredAt
            }).SingleAsync(cancellationToken);

        var persistedItems = await dbContext.IncorporationContents
            .AsNoTracking()
            .Where(content => content.IncorporationId == incorporationId)
            .OrderBy(content => content.ProductId)
            .ToArrayAsync(cancellationToken);
        var items = persistedItems
            .Select(content => new ConfirmedItemResponse(
                content.ProductId,
                content.Quantity,
                content.AppliedPrice.ToString(CultureInfo.InvariantCulture)))
            .ToArray();

        return new FirstConfirmationResponse(
            header.OrderId.ToString("D"),
            header.ConfirmedContext,
            new FirstIncorporationResponse(
                header.IncorporationId,
                header.OccurredAt,
                items));
    }

    private static FirstConfirmationValidation Validate(FirstConfirmationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Context))
        {
            return FirstConfirmationValidation.Invalid(
                FirstConfirmationResult.ContextRequired());
        }

        if (request.Items is null || request.Items.Count == 0)
        {
            return FirstConfirmationValidation.Invalid(
                FirstConfirmationResult.CompositionEmpty());
        }

        var items = new List<ValidatedFirstConfirmationItem>(request.Items.Count);
        foreach (var item in request.Items)
        {
            if (item is null || item.ProductId == Guid.Empty)
            {
                return FirstConfirmationValidation.Invalid(
                    FirstConfirmationResult.RequestInvalid());
            }

            if (item.Quantity <= 0)
            {
                return FirstConfirmationValidation.Invalid(
                    FirstConfirmationResult.QuantityInvalid(item.ProductId));
            }

            items.Add(new ValidatedFirstConfirmationItem(
                item.ProductId,
                item.Quantity));
        }

        var duplicateProduct = items
            .GroupBy(item => item.ProductId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateProduct is not null)
        {
            return FirstConfirmationValidation.Invalid(
                FirstConfirmationResult.DuplicateProduct(duplicateProduct.Key));
        }

        return FirstConfirmationValidation.Valid(new ValidatedFirstConfirmationIntent(
            request.Context.Trim(),
            items.OrderBy(item => item.ProductId).ToArray()));
    }

    private static bool Matches(
        FirstConfirmationCommand command,
        IReadOnlyList<FirstConfirmationCommandContent> contents,
        ValidatedFirstConfirmationIntent intent) =>
        string.Equals(command.IntentContext, intent.Context, StringComparison.Ordinal) &&
        contents.Count == intent.Items.Count &&
        contents.Zip(intent.Items).All(pair =>
            pair.First.ProductId == pair.Second.ProductId &&
            pair.First.Quantity == pair.Second.Quantity);

    private static long CreateTransactionLockKey(Guid idempotencyKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);

        return FirstConfirmationLockNamespace ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}

internal sealed record FirstConfirmationValidation(
    ValidatedFirstConfirmationIntent? Intent,
    FirstConfirmationResult? Error)
{
    internal static FirstConfirmationValidation Valid(
        ValidatedFirstConfirmationIntent intent) => new(intent, null);

    internal static FirstConfirmationValidation Invalid(
        FirstConfirmationResult error) => new(null, error);
}

internal sealed record ValidatedFirstConfirmationIntent(
    string Context,
    IReadOnlyList<ValidatedFirstConfirmationItem> Items);

internal sealed record ValidatedFirstConfirmationItem(Guid ProductId, int Quantity);

internal sealed record FirstConfirmationResult(
    FirstConfirmationOutcome Outcome,
    FirstConfirmationResponse? Response,
    Guid? ProductId)
{
    internal static FirstConfirmationResult Confirmed(FirstConfirmationResponse response) =>
        new(FirstConfirmationOutcome.Confirmed, response, null);

    internal static FirstConfirmationResult ContextRequired() =>
        new(FirstConfirmationOutcome.ContextRequired, null, null);

    internal static FirstConfirmationResult CompositionEmpty() =>
        new(FirstConfirmationOutcome.CompositionEmpty, null, null);

    internal static FirstConfirmationResult RequestInvalid() =>
        new(FirstConfirmationOutcome.RequestInvalid, null, null);

    internal static FirstConfirmationResult QuantityInvalid(Guid productId) =>
        new(FirstConfirmationOutcome.QuantityInvalid, null, productId);

    internal static FirstConfirmationResult DuplicateProduct(Guid productId) =>
        new(FirstConfirmationOutcome.DuplicateProduct, null, productId);

    internal static FirstConfirmationResult ProductNotCurrent(Guid productId) =>
        new(FirstConfirmationOutcome.ProductNotCurrent, null, productId);

    internal static FirstConfirmationResult ProductUnavailable(Guid productId) =>
        new(FirstConfirmationOutcome.ProductUnavailable, null, productId);

    internal static FirstConfirmationResult RequiresPreparationNotSupported(Guid productId) =>
        new(FirstConfirmationOutcome.RequiresPreparationNotSupported, null, productId);

    internal static FirstConfirmationResult IdempotencyConflict() =>
        new(FirstConfirmationOutcome.IdempotencyConflict, null, null);
}

internal enum FirstConfirmationOutcome
{
    Confirmed,
    ContextRequired,
    CompositionEmpty,
    RequestInvalid,
    QuantityInvalid,
    DuplicateProduct,
    ProductNotCurrent,
    ProductUnavailable,
    RequiresPreparationNotSupported,
    IdempotencyConflict
}
