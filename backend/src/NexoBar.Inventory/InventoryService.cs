using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.Inventory;

internal sealed class InventoryService(
    InventoryDbContext dbContext,
    IInventoryAuthorization authorization)
{
    private const long ItemCreationLockNamespace = 0x494E564352454154;

    internal async Task<CreateInventoryItemResult> CreateItemAsync(
        Guid idempotencyKey,
        CreateInventoryItemRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedName = InventoryItem.CanonicalNameFingerprint(
            request.OperationalName);
        var canonicalUnit = InventoryItem.CanonicalUnitFingerprint(
            request.OperationalUnit);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var lockKey = CreateTransactionLockKey(idempotencyKey);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);

        var actor = await authorization.StabilizeSessionAndIdentityAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (actor is null)
        {
            return CreateInventoryItemResult.Unauthenticated();
        }

        var existingCommand = await dbContext.InventoryItemCreationCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existingCommand.Matches(
                actor.IdentityId,
                normalizedName,
                canonicalUnit)
                ? CreateInventoryItemResult.Created(Map(existingCommand))
                : CreateInventoryItemResult.IdempotencyConflict();
        }

        if (!await authorization.StabilizeInventoryConfigurationAsync(
                actor.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            return CreateInventoryItemResult.Forbidden();
        }

        var validation = InventoryItem.TryCreate(
            request.OperationalName,
            request.OperationalUnit);
        if (validation.Error == InventoryItemValidationError.OperationalNameInvalid)
        {
            return CreateInventoryItemResult.InvalidName();
        }

        if (validation.Error == InventoryItemValidationError.OperationalUnitInvalid)
        {
            return CreateInventoryItemResult.InvalidUnit();
        }

        var item = validation.Item!;
        var response = Map(item);
        dbContext.InventoryItems.Add(item);
        dbContext.InventoryItemCreationCommands.Add(
            new InventoryItemCreationCommand(
                idempotencyKey,
                actor.IdentityId,
                normalizedName,
                canonicalUnit,
                response));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "UX_inventory_items_normalized_name"
            })
        {
            await transaction.RollbackAsync(cancellationToken);
            return CreateInventoryItemResult.DuplicateName();
        }

        return CreateInventoryItemResult.Created(response);
    }

    internal Task<InventoryReadResult<InventoryConfigurationItemResponse>>
        ListConfigurationItemsAsync(CancellationToken cancellationToken) =>
        ListAuthorizedAsync(
            static (authorization, identityId, transaction, token) =>
                authorization.StabilizeInventoryConfigurationAsync(
                    identityId,
                    transaction,
                    token),
            static item => new InventoryConfigurationItemResponse(
                item.Id,
                item.OperationalName,
                item.OperationalUnit.Value),
            cancellationToken);

    internal Task<InventoryReadResult<InventoryOperationalItemResponse>>
        ListOperationalItemsAsync(CancellationToken cancellationToken) =>
        ListAuthorizedAsync(
            static (authorization, identityId, transaction, token) =>
                authorization.StabilizeInventoryOperationAsync(
                    identityId,
                    transaction,
                    token),
            static item => new InventoryOperationalItemResponse(
                item.Id,
                item.OperationalName,
                item.OperationalUnit.Value,
                FormatQuantity(item.CurrentRegisteredQuantity),
                item.CurrentRegisteredQuantity is not null,
                item.CurrentRegisteredQuantity < 0,
                item.MovementRevision),
            cancellationToken);

    private async Task<InventoryReadResult<TResponse>> ListAuthorizedAsync<TResponse>(
        Func<IInventoryAuthorization, Guid, System.Data.Common.DbTransaction,
            CancellationToken, Task<bool>> stabilizeResponsibility,
        Func<InventoryItem, TResponse> map,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var dbTransaction = transaction.GetDbTransaction();
        var actor = await authorization.StabilizeSessionAndIdentityAsync(
            dbTransaction,
            cancellationToken);
        if (actor is null)
        {
            return InventoryReadResult<TResponse>.Unauthenticated();
        }

        if (!await stabilizeResponsibility(
                authorization,
                actor.IdentityId,
                dbTransaction,
                cancellationToken))
        {
            return InventoryReadResult<TResponse>.Forbidden();
        }

        var items = await dbContext.InventoryItems
            .AsNoTracking()
            .OrderBy(item => item.OperationalName)
            .ThenBy(item => item.Id)
            .ToArrayAsync(cancellationToken);
        var response = items.Select(map).ToArray();
        await transaction.CommitAsync(cancellationToken);
        return InventoryReadResult<TResponse>.Succeeded(response);
    }

    private static InventoryItemResponse Map(InventoryItem item) =>
        new(
            item.Id,
            item.OperationalName,
            item.OperationalUnit.Value,
            FormatQuantity(item.CurrentRegisteredQuantity),
            item.MovementRevision);

    private static InventoryItemResponse Map(InventoryItemCreationCommand command) =>
        new(
            command.ResultItemId,
            command.ResultOperationalName,
            command.ResultOperationalUnit,
            FormatQuantity(command.ResultCurrentRegisteredQuantity),
            command.ResultMovementRevision);

    private static string? FormatQuantity(decimal? quantity) =>
        quantity?.ToString("0.############", CultureInfo.InvariantCulture);

    private static long CreateTransactionLockKey(Guid idempotencyKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);
        return ItemCreationLockNamespace ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}

internal sealed record CreateInventoryItemResult(
    CreateInventoryItemOutcome Outcome,
    InventoryItemResponse? Item)
{
    internal static CreateInventoryItemResult Created(InventoryItemResponse item) =>
        new(CreateInventoryItemOutcome.Created, item);

    internal static CreateInventoryItemResult Unauthenticated() =>
        new(CreateInventoryItemOutcome.Unauthenticated, null);

    internal static CreateInventoryItemResult Forbidden() =>
        new(CreateInventoryItemOutcome.Forbidden, null);

    internal static CreateInventoryItemResult InvalidName() =>
        new(CreateInventoryItemOutcome.InvalidName, null);

    internal static CreateInventoryItemResult InvalidUnit() =>
        new(CreateInventoryItemOutcome.InvalidUnit, null);

    internal static CreateInventoryItemResult DuplicateName() =>
        new(CreateInventoryItemOutcome.DuplicateName, null);

    internal static CreateInventoryItemResult IdempotencyConflict() =>
        new(CreateInventoryItemOutcome.IdempotencyConflict, null);
}

internal enum CreateInventoryItemOutcome
{
    Created,
    Unauthenticated,
    Forbidden,
    InvalidName,
    InvalidUnit,
    DuplicateName,
    IdempotencyConflict
}

internal sealed record InventoryReadResult<TResponse>(
    InventoryReadOutcome Outcome,
    IReadOnlyList<TResponse>? Items)
{
    internal static InventoryReadResult<TResponse> Succeeded(
        IReadOnlyList<TResponse> items) =>
        new(InventoryReadOutcome.Succeeded, items);

    internal static InventoryReadResult<TResponse> Unauthenticated() =>
        new(InventoryReadOutcome.Unauthenticated, null);

    internal static InventoryReadResult<TResponse> Forbidden() =>
        new(InventoryReadOutcome.Forbidden, null);
}

internal enum InventoryReadOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden
}
