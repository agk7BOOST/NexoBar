using System.Buffers.Binary;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.Catalog;

namespace NexoBar.OrderOperations;

internal sealed class SubsequentConfirmationService(
    OrderOperationsDbContext dbContext,
    IOrderConfirmationCatalog catalog)
{
    private const long SubsequentConfirmationLockNamespace = 0x535542434F4E4600;

    internal async Task<SubsequentConfirmationResult> ConfirmAsync(
        Guid idempotencyKey,
        Guid orderId,
        SubsequentConfirmationRequest request,
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

        var existingCommand = await dbContext.SubsequentConfirmationCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);

        if (existingCommand is not null)
        {
            var existingContents = await dbContext.SubsequentConfirmationCommandContents
                .AsNoTracking()
                .Where(content => content.IdempotencyKey == idempotencyKey)
                .OrderBy(content => content.LineOrdinal)
                .ToArrayAsync(cancellationToken);

            if (!Matches(existingCommand, existingContents, orderId, intent))
            {
                await transaction.CommitAsync(cancellationToken);
                return SubsequentConfirmationResult.IdempotencyConflict();
            }

            var replay = await LoadOriginalResponseAsync(
                existingCommand.ResultIncorporationId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return SubsequentConfirmationResult.Confirmed(replay);
        }

        var order = await dbContext.Orders
            .FromSqlInterpolated(
                $"""
                SELECT id, context
                FROM order_operations.orders
                WHERE id = {orderId}
                FOR UPDATE
                """)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        if (order is null)
        {
            return SubsequentConfirmationResult.OrderNotFound();
        }

        var catalogProducts = await catalog.ReadProductsAsync(
            intent.Items.Select(item => item.ProductId).Distinct().ToArray(),
            transaction.GetDbTransaction(),
            cancellationToken);
        var productsById = catalogProducts.ToDictionary(product => product.ProductId);

        foreach (var item in intent.Items)
        {
            if (!productsById.TryGetValue(item.ProductId, out var product) ||
                !product.IsActive)
            {
                return SubsequentConfirmationResult.ProductNotCurrent(item.ProductId);
            }

            if (!product.IsAvailable)
            {
                return SubsequentConfirmationResult.ProductUnavailable(item.ProductId);
            }

            if (!product.RequiresPreparation && item.Instruction is not null)
            {
                return SubsequentConfirmationResult.InstructionRequiresPreparation(
                    item.ProductId);
            }
        }

        var currentMaximumOrdinal = await dbContext.Incorporations
            .Where(incorporation => incorporation.OrderId == orderId)
            .MaxAsync(
                incorporation => (int?)incorporation.Ordinal,
                cancellationToken) ?? 0;
        var nextOrdinal = checked(currentMaximumOrdinal + 1);

        var incorporationId = Guid.CreateVersion7();
        var utcNow = DateTimeOffset.UtcNow;
        var confirmedAt = new DateTimeOffset(
            utcNow.Ticks - (utcNow.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);

        dbContext.Incorporations.Add(new Incorporation(
            incorporationId,
            orderId,
            nextOrdinal));
        dbContext.ConfirmationHistory.Add(new ConfirmationHistory(
            Guid.CreateVersion7(),
            incorporationId,
            order.Context,
            confirmedAt));
        dbContext.SubsequentConfirmationCommands.Add(new SubsequentConfirmationCommand(
            idempotencyKey,
            orderId,
            incorporationId));

        var responseItems = new List<ConfirmedItemResponse>(intent.Items.Count);
        for (var index = 0; index < intent.Items.Count; index++)
        {
            var item = intent.Items[index];
            var contentOrdinal = checked(index + 1);
            var product = productsById[item.ProductId];
            var creation = ConfirmedContentFactory
                .CreateConfirmedContentAndPreparationWork(
                incorporationId,
                contentOrdinal,
                item.Quantity,
                item.Instruction,
                product);
            dbContext.IncorporationContents.Add(creation.Content);
            if (creation.PreparationWork is not null)
            {
                dbContext.PreparationWork.Add(creation.PreparationWork);
            }
            dbContext.SubsequentConfirmationCommandContents.Add(
                new SubsequentConfirmationCommandContent(
                    idempotencyKey,
                    contentOrdinal,
                    item.ProductId,
                    item.Quantity,
                    item.Instruction));
            responseItems.Add(new ConfirmedItemResponse(
                item.ProductId,
                item.Quantity,
                product.Price.ToString(CultureInfo.InvariantCulture),
                item.Instruction));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return SubsequentConfirmationResult.Confirmed(
            new SubsequentConfirmationResponse(
                orderId.ToString("D"),
                new SubsequentIncorporationResponse(
                    incorporationId,
                    nextOrdinal,
                    confirmedAt,
                    responseItems)));
    }

    private async Task<SubsequentConfirmationResponse> LoadOriginalResponseAsync(
        Guid incorporationId,
        CancellationToken cancellationToken)
    {
        var header = await (
            from incorporation in dbContext.Incorporations.AsNoTracking()
            join history in dbContext.ConfirmationHistory.AsNoTracking()
                on incorporation.Id equals history.IncorporationId
            where incorporation.Id == incorporationId
            select new
            {
                incorporation.OrderId,
                incorporation.Id,
                incorporation.Ordinal,
                history.OccurredAt
            }).SingleAsync(cancellationToken);

        var persistedItems = await dbContext.IncorporationContents
            .AsNoTracking()
            .Where(content => content.IncorporationId == incorporationId)
            .OrderBy(content => content.ContentOrdinal)
            .ToArrayAsync(cancellationToken);
        var items = persistedItems
            .Select(content => new ConfirmedItemResponse(
                content.ProductId,
                content.Quantity,
                content.AppliedPrice.ToString(CultureInfo.InvariantCulture),
                content.Instruction))
            .ToArray();

        return new SubsequentConfirmationResponse(
            header.OrderId.ToString("D"),
            new SubsequentIncorporationResponse(
                header.Id,
                header.Ordinal,
                header.OccurredAt,
                items));
    }

    private static SubsequentConfirmationValidation Validate(
        SubsequentConfirmationRequest request)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            return SubsequentConfirmationValidation.Invalid(
                SubsequentConfirmationResult.CompositionEmpty());
        }

        var items = new List<ValidatedSubsequentConfirmationItem>(request.Items.Count);
        foreach (var item in request.Items)
        {
            if (item is null || item.ProductId == Guid.Empty)
            {
                return SubsequentConfirmationValidation.Invalid(
                    SubsequentConfirmationResult.RequestInvalid());
            }

            if (item.Quantity <= 0)
            {
                return SubsequentConfirmationValidation.Invalid(
                    SubsequentConfirmationResult.QuantityInvalid(item.ProductId));
            }

            items.Add(new ValidatedSubsequentConfirmationItem(
                item.ProductId,
                item.Quantity,
                ConfirmationInstruction.Canonicalize(item.Instruction)));
        }

        var duplicateLine = items
            .GroupBy(item => (item.ProductId, item.Instruction))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateLine is not null)
        {
            return SubsequentConfirmationValidation.Invalid(
                SubsequentConfirmationResult.DuplicateLine(duplicateLine.Key.ProductId));
        }

        return SubsequentConfirmationValidation.Valid(
            new ValidatedSubsequentConfirmationIntent(
                items.OrderBy(item => item.ProductId)
                    .ThenBy(item => item.Instruction is null ? 0 : 1)
                    .ThenBy(item => item.Instruction, StringComparer.Ordinal)
                    .ToArray()));
    }

    private static bool Matches(
        SubsequentConfirmationCommand command,
        IReadOnlyList<SubsequentConfirmationCommandContent> contents,
        Guid orderId,
        ValidatedSubsequentConfirmationIntent intent) =>
        command.IntentOrderId == orderId &&
        contents.Count == intent.Items.Count &&
        contents.Zip(intent.Items).All(pair =>
            pair.First.ProductId == pair.Second.ProductId &&
            pair.First.Quantity == pair.Second.Quantity &&
            string.Equals(
                pair.First.Instruction,
                pair.Second.Instruction,
                StringComparison.Ordinal));

    private static long CreateTransactionLockKey(Guid idempotencyKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);

        return SubsequentConfirmationLockNamespace ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}

internal sealed record SubsequentConfirmationValidation(
    ValidatedSubsequentConfirmationIntent? Intent,
    SubsequentConfirmationResult? Error)
{
    internal static SubsequentConfirmationValidation Valid(
        ValidatedSubsequentConfirmationIntent intent) => new(intent, null);

    internal static SubsequentConfirmationValidation Invalid(
        SubsequentConfirmationResult error) => new(null, error);
}

internal sealed record ValidatedSubsequentConfirmationIntent(
    IReadOnlyList<ValidatedSubsequentConfirmationItem> Items);

internal sealed record ValidatedSubsequentConfirmationItem(
    Guid ProductId,
    int Quantity,
    string? Instruction);

internal sealed record SubsequentConfirmationResult(
    SubsequentConfirmationOutcome Outcome,
    SubsequentConfirmationResponse? Response,
    Guid? ProductId)
{
    internal static SubsequentConfirmationResult Confirmed(
        SubsequentConfirmationResponse response) =>
        new(SubsequentConfirmationOutcome.Confirmed, response, null);

    internal static SubsequentConfirmationResult CompositionEmpty() =>
        new(SubsequentConfirmationOutcome.CompositionEmpty, null, null);

    internal static SubsequentConfirmationResult RequestInvalid() =>
        new(SubsequentConfirmationOutcome.RequestInvalid, null, null);

    internal static SubsequentConfirmationResult QuantityInvalid(Guid productId) =>
        new(SubsequentConfirmationOutcome.QuantityInvalid, null, productId);

    internal static SubsequentConfirmationResult DuplicateLine(Guid productId) =>
        new(SubsequentConfirmationOutcome.DuplicateLine, null, productId);

    internal static SubsequentConfirmationResult OrderNotFound() =>
        new(SubsequentConfirmationOutcome.OrderNotFound, null, null);

    internal static SubsequentConfirmationResult ProductNotCurrent(Guid productId) =>
        new(SubsequentConfirmationOutcome.ProductNotCurrent, null, productId);

    internal static SubsequentConfirmationResult ProductUnavailable(Guid productId) =>
        new(SubsequentConfirmationOutcome.ProductUnavailable, null, productId);

    internal static SubsequentConfirmationResult InstructionRequiresPreparation(
        Guid productId) =>
        new(SubsequentConfirmationOutcome.InstructionRequiresPreparation, null, productId);

    internal static SubsequentConfirmationResult IdempotencyConflict() =>
        new(SubsequentConfirmationOutcome.IdempotencyConflict, null, null);
}

internal enum SubsequentConfirmationOutcome
{
    Confirmed,
    CompositionEmpty,
    RequestInvalid,
    QuantityInvalid,
    DuplicateLine,
    OrderNotFound,
    ProductNotCurrent,
    ProductUnavailable,
    InstructionRequiresPreparation,
    IdempotencyConflict
}
