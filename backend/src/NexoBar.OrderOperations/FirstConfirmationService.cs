using System.Buffers.Binary;
using System.Globalization;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OperationalConfiguration;

namespace NexoBar.OrderOperations;

internal sealed class FirstConfirmationService(
    OrderOperationsDbContext dbContext,
    IOrderConfirmationCatalog catalog,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IOrderOperationsCapabilityStabilizer capabilityStabilizer,
    IOperationalInterventionCapabilityStabilizer operationalInterventionCapabilityStabilizer,
    IOrderContextConfiguration contextConfiguration,
    IPreparationDestinationInvalidationPublisher invalidations)
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
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var lockKey = CreateTransactionLockKey(idempotencyKey);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);

        var stabilizedSession = await sessionStabilizer.StabilizeAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (stabilizedSession is null)
        {
            return FirstConfirmationResult.Unauthenticated();
        }

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
                .OrderBy(content => content.LineOrdinal)
                .ToArrayAsync(cancellationToken);

            if (!Matches(
                    existingCommand,
                    existingContents,
                    stabilizedSession.IdentityId,
                    intent))
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

        if (!await capabilityStabilizer.StabilizeResponsibilityAsync(
                stabilizedSession.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            return FirstConfirmationResult.Forbidden();
        }

        if (intent.Items.Any(item => item.UnavailableProductExceptionRequested) &&
            !await operationalInterventionCapabilityStabilizer.StabilizeResponsibilityAsync(
                stabilizedSession.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            return FirstConfirmationResult.OperationalInterventionRequired();
        }

        if (intent.ContextId is null)
        {
            return FirstConfirmationResult.ContextIdRequired();
        }

        var configuredContext = await contextConfiguration.ResolveConfiguredContextAsync(
            intent.ContextId.Value,
            cancellationToken);
        if (configuredContext is null)
        {
            return FirstConfirmationResult.ContextNotCurrent();
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
                return FirstConfirmationResult.ProductNotCurrent(item.ProductId);
            }

            if (!product.IsAvailable && !item.UnavailableProductExceptionRequested)
            {
                return FirstConfirmationResult.ProductUnavailable(item.ProductId);
            }

            if (!product.RequiresPreparation && item.Instruction is not null)
            {
                return FirstConfirmationResult.InstructionRequiresPreparation(
                    item.ProductId);
            }
        }

        var orderId = Guid.CreateVersion7();
        var incorporationId = Guid.CreateVersion7();
        var utcNow = DateTimeOffset.UtcNow;
        var confirmedAt = new DateTimeOffset(
            utcNow.Ticks - (utcNow.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);

        dbContext.Orders.Add(new Order(
            orderId,
            configuredContext.ContextId,
            configuredContext.OperationalName));
        dbContext.Incorporations.Add(new Incorporation(incorporationId, orderId, ordinal: 1));
        dbContext.ConfirmationHistory.Add(new ConfirmationHistory(
            Guid.CreateVersion7(),
            incorporationId,
            configuredContext.ContextId,
            configuredContext.OperationalName,
            stabilizedSession.IdentityId,
            confirmedAt));
        dbContext.FirstConfirmationCommands.Add(new FirstConfirmationCommand(
            idempotencyKey,
            stabilizedSession.IdentityId,
            configuredContext.ContextId,
            configuredContext.OperationalName,
            incorporationId));

        var responseItems = new List<ConfirmedItemResponse>(intent.Items.Count);
        var affectedDestinations = new HashSet<Guid>();
        for (var index = 0; index < intent.Items.Count; index++)
        {
            var item = intent.Items[index];
            var contentOrdinal = checked(index + 1);
            var product = productsById[item.ProductId];
            var unavailableProductExceptionApplied =
                !product.IsAvailable && item.UnavailableProductExceptionRequested;
            var creation = ConfirmedContentFactory
                .CreateConfirmedContent(
                    incorporationId,
                    contentOrdinal,
                    item.Quantity,
                    item.Instruction,
                    unavailableProductExceptionApplied,
                    product);
            dbContext.IncorporationContents.Add(creation.Content);
            if (creation.PreparationWork is not null)
            {
                dbContext.PreparationWork.Add(creation.PreparationWork);
                affectedDestinations.Add(creation.PreparationWork.PreparationResponsibilityId);
            }
            dbContext.DeliveryStates.Add(creation.DeliveryState);
            dbContext.ContentQuantityStates.Add(creation.QuantityState);
            dbContext.ContentAppliedPriceStates.Add(creation.AppliedPriceState);
            dbContext.FirstConfirmationCommandContents.Add(
                new FirstConfirmationCommandContent(
                    idempotencyKey,
                    contentOrdinal,
                    item.ProductId,
                    item.Quantity,
                    item.Instruction,
                    item.UnavailableProductExceptionRequested));
            responseItems.Add(new ConfirmedItemResponse(
                item.ProductId,
                item.Quantity,
                product.Price.ToString(CultureInfo.InvariantCulture),
                item.Instruction,
                unavailableProductExceptionApplied));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        invalidations.Publish(affectedDestinations);

        return FirstConfirmationResult.Confirmed(
            new FirstConfirmationResponse(
                orderId.ToString("D"),
                configuredContext.ContextId,
                configuredContext.OperationalName,
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
                history.ConfirmedContextId,
                history.ConfirmedContext,
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
                content.Instruction,
                content.UnavailableProductExceptionApplied))
            .ToArray();

        return new FirstConfirmationResponse(
            header.OrderId.ToString("D"),
            header.ConfirmedContextId,
            header.ConfirmedContext,
            new FirstIncorporationResponse(
                header.IncorporationId,
                header.OccurredAt,
                items));
    }

    private static FirstConfirmationValidation Validate(FirstConfirmationRequest request)
    {
        var legacyContext = string.IsNullOrWhiteSpace(request.Context)
            ? null
            : request.Context.Trim();
        if (request.ContextId is null && legacyContext is null)
        {
            return FirstConfirmationValidation.Invalid(
                FirstConfirmationResult.ContextRequired());
        }

        if (request.ContextId == Guid.Empty ||
            (request.ContextId is not null && legacyContext is not null))
        {
            return FirstConfirmationValidation.Invalid(
                FirstConfirmationResult.RequestInvalid());
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
                item.Quantity,
                ConfirmationInstruction.Canonicalize(item.Instruction),
                item.UnavailableProductExceptionRequested));
        }

        var duplicateLine = items
            .GroupBy(item => (item.ProductId, item.Instruction))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateLine is not null)
        {
            return FirstConfirmationValidation.Invalid(
                FirstConfirmationResult.DuplicateLine(duplicateLine.Key.ProductId));
        }

        return FirstConfirmationValidation.Valid(new ValidatedFirstConfirmationIntent(
            request.ContextId,
            legacyContext,
            items.OrderBy(item => item.ProductId)
                .ThenBy(item => item.Instruction is null ? 0 : 1)
                .ThenBy(item => item.Instruction, StringComparer.Ordinal)
                .ToArray()));
    }

    private static bool Matches(
        FirstConfirmationCommand command,
        IReadOnlyList<FirstConfirmationCommandContent> contents,
        Guid actorIdentityId,
        ValidatedFirstConfirmationIntent intent) =>
        command.ActorIdentityId == actorIdentityId &&
        MatchesContext(command, intent) &&
        contents.Count == intent.Items.Count &&
        contents.Zip(intent.Items).All(pair =>
            pair.First.ProductId == pair.Second.ProductId &&
            pair.First.Quantity == pair.Second.Quantity &&
            pair.First.IntentUnavailableProductExceptionRequested ==
                pair.Second.UnavailableProductExceptionRequested &&
            string.Equals(
                pair.First.Instruction,
                pair.Second.Instruction,
                StringComparison.Ordinal));

    private static bool MatchesContext(
        FirstConfirmationCommand command,
        ValidatedFirstConfirmationIntent intent) =>
        intent.ContextId is not null
            ? command.IntentContextId == intent.ContextId.Value
            : intent.LegacyContext is not null &&
                string.Equals(
                    command.IntentContext,
                    intent.LegacyContext,
                    StringComparison.Ordinal);

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
    Guid? ContextId,
    string? LegacyContext,
    IReadOnlyList<ValidatedFirstConfirmationItem> Items);

internal sealed record ValidatedFirstConfirmationItem(
    Guid ProductId,
    int Quantity,
    string? Instruction,
    bool UnavailableProductExceptionRequested);

internal sealed record FirstConfirmationResult(
    FirstConfirmationOutcome Outcome,
    FirstConfirmationResponse? Response,
    Guid? ProductId)
{
    internal static FirstConfirmationResult Confirmed(FirstConfirmationResponse response) =>
        new(FirstConfirmationOutcome.Confirmed, response, null);

    internal static FirstConfirmationResult ContextRequired() =>
        new(FirstConfirmationOutcome.ContextRequired, null, null);

    internal static FirstConfirmationResult ContextNotCurrent() =>
        new(FirstConfirmationOutcome.ContextNotCurrent, null, null);

    internal static FirstConfirmationResult ContextIdRequired() =>
        new(FirstConfirmationOutcome.ContextIdRequired, null, null);

    internal static FirstConfirmationResult CompositionEmpty() =>
        new(FirstConfirmationOutcome.CompositionEmpty, null, null);

    internal static FirstConfirmationResult RequestInvalid() =>
        new(FirstConfirmationOutcome.RequestInvalid, null, null);

    internal static FirstConfirmationResult QuantityInvalid(Guid productId) =>
        new(FirstConfirmationOutcome.QuantityInvalid, null, productId);

    internal static FirstConfirmationResult DuplicateLine(Guid productId) =>
        new(FirstConfirmationOutcome.DuplicateLine, null, productId);

    internal static FirstConfirmationResult ProductNotCurrent(Guid productId) =>
        new(FirstConfirmationOutcome.ProductNotCurrent, null, productId);

    internal static FirstConfirmationResult ProductUnavailable(Guid productId) =>
        new(FirstConfirmationOutcome.ProductUnavailable, null, productId);

    internal static FirstConfirmationResult InstructionRequiresPreparation(
        Guid productId) =>
        new(FirstConfirmationOutcome.InstructionRequiresPreparation, null, productId);

    internal static FirstConfirmationResult IdempotencyConflict() =>
        new(FirstConfirmationOutcome.IdempotencyConflict, null, null);

    internal static FirstConfirmationResult Unauthenticated() =>
        new(FirstConfirmationOutcome.Unauthenticated, null, null);

    internal static FirstConfirmationResult Forbidden() =>
        new(FirstConfirmationOutcome.Forbidden, null, null);

    internal static FirstConfirmationResult OperationalInterventionRequired() =>
        new(FirstConfirmationOutcome.OperationalInterventionRequired, null, null);
}

internal enum FirstConfirmationOutcome
{
    Confirmed,
    ContextRequired,
    ContextNotCurrent,
    ContextIdRequired,
    CompositionEmpty,
    RequestInvalid,
    QuantityInvalid,
    DuplicateLine,
    ProductNotCurrent,
    ProductUnavailable,
    InstructionRequiresPreparation,
    IdempotencyConflict,
    Unauthenticated,
    Forbidden,
    OperationalInterventionRequired
}
