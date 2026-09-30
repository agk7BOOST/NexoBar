using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace NexoBar.OperationalConfiguration;

internal sealed class OperationalContextService(
    OperationalConfigurationDbContext dbContext,
    IOperationalConfigurationAuthorization authorization,
    IOrderContextLookupAuthorization orderLookupAuthorization)
{
    private const long CreationLockNamespace = 0x4354584352454100;

    internal async Task<CreateOperationalContextResult> CreateAsync(
        Guid idempotencyKey,
        CreateOperationalContextRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OperationalName))
        {
            return CreateOperationalContextResult.Invalid();
        }

        var operationalName = request.OperationalName.Trim();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var lockKey = CreateTransactionLockKey(idempotencyKey);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);

        var actor = await authorization.StabilizeSessionAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (actor is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return CreateOperationalContextResult.AuthenticationRequired();
        }

        var existingCommand = await dbContext.OperationalContextCreationCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existingCommand.Matches(actor.IdentityId, operationalName)
                ? CreateOperationalContextResult.Created(Map(existingCommand))
                : CreateOperationalContextResult.IdempotencyConflict();
        }

        if (await dbContext.OperationalContextLifecycleCommands.AnyAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken))
            return CreateOperationalContextResult.IdempotencyConflict();

        if (!await authorization.StabilizeGeneralConfigurationAsync(
                actor.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return CreateOperationalContextResult.GeneralConfigurationRequired();
        }

        var context = new OperationalContext(Guid.CreateVersion7(), operationalName);
        var response = Map(context);
        dbContext.Contexts.Add(context);
        dbContext.OperationalContextCreationCommands.Add(
            new OperationalContextCreationCommand(
                idempotencyKey,
                actor.IdentityId,
                operationalName,
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
                ConstraintName: "UX_operational_configuration_contexts_normalized_name"
            })
        {
            await transaction.RollbackAsync(cancellationToken);
            return CreateOperationalContextResult.DuplicateName();
        }

        return CreateOperationalContextResult.Created(response);
    }

    internal async Task<IReadOnlyList<OperationalContextReference>> ListAdministrativeAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var actor = await authorization.StabilizeSessionAsync(
            transaction.GetDbTransaction(), cancellationToken);
        if (actor is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new OperationalContextAuthorizationException(
                CreateOperationalContextOutcome.AuthenticationRequired);
        }

        if (!await authorization.StabilizeGeneralConfigurationAsync(
                actor.IdentityId, transaction.GetDbTransaction(), cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new OperationalContextAuthorizationException(
                CreateOperationalContextOutcome.GeneralConfigurationRequired);
        }

        var contexts = await dbContext.Contexts.AsNoTracking()
            .OrderBy(x => x.OperationalName).ThenBy(x => x.Id)
            .Select(x => new OperationalContextReference(x.Id, x.OperationalName, x.IsActive))
            .ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return contexts;
    }

    internal async Task<OperationalContextListResult> ListForOrderOperationsAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var authorizationOutcome = await orderLookupAuthorization.AuthorizeAsync(
            transaction.GetDbTransaction(), cancellationToken);
        if (authorizationOutcome != OrderContextLookupAuthorizationOutcome.Authorized)
        {
            await transaction.RollbackAsync(cancellationToken);
            return authorizationOutcome == OrderContextLookupAuthorizationOutcome.Unauthenticated
                ? OperationalContextListResult.Unauthenticated()
                : OperationalContextListResult.Forbidden();
        }

        var contexts = await new OperationalContextLookup(dbContext)
            .ListConfiguredContextsAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationalContextListResult.Succeeded(contexts);
    }

    private static long CreateTransactionLockKey(Guid idempotencyKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);
        return CreationLockNamespace ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }

    private static OperationalContextReference Map(OperationalContext context) =>
        new(context.Id, context.OperationalName, context.IsActive);

    private static OperationalContextReference Map(
        OperationalContextCreationCommand command) =>
        new(command.ResultContextId, command.ResultOperationalName);
}

internal sealed record CreateOperationalContextResult(
    CreateOperationalContextOutcome Outcome,
    OperationalContextReference? Context)
{
    internal static CreateOperationalContextResult Created(OperationalContextReference context) =>
        new(CreateOperationalContextOutcome.Created, context);
    internal static CreateOperationalContextResult Invalid() =>
        new(CreateOperationalContextOutcome.Invalid, null);
    internal static CreateOperationalContextResult DuplicateName() =>
        new(CreateOperationalContextOutcome.DuplicateName, null);
    internal static CreateOperationalContextResult IdempotencyConflict() =>
        new(CreateOperationalContextOutcome.IdempotencyConflict, null);
    internal static CreateOperationalContextResult AuthenticationRequired() =>
        new(CreateOperationalContextOutcome.AuthenticationRequired, null);
    internal static CreateOperationalContextResult GeneralConfigurationRequired() =>
        new(CreateOperationalContextOutcome.GeneralConfigurationRequired, null);
}

internal enum CreateOperationalContextOutcome
{
    Created,
    Invalid,
    DuplicateName,
    IdempotencyConflict,
    AuthenticationRequired,
    GeneralConfigurationRequired
}

internal sealed class OperationalContextAuthorizationException(
    CreateOperationalContextOutcome outcome) : Exception
{
    internal CreateOperationalContextOutcome Outcome { get; } = outcome;
}

internal sealed record OperationalContextListResult(
    OrderContextLookupAuthorizationOutcome Outcome,
    IReadOnlyList<OperationalContextReference>? Contexts)
{
    internal static OperationalContextListResult Succeeded(
        IReadOnlyList<OperationalContextReference> contexts) =>
        new(OrderContextLookupAuthorizationOutcome.Authorized, contexts);
    internal static OperationalContextListResult Unauthenticated() =>
        new(OrderContextLookupAuthorizationOutcome.Unauthenticated, null);
    internal static OperationalContextListResult Forbidden() =>
        new(OrderContextLookupAuthorizationOutcome.Forbidden, null);
}
