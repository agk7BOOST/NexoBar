using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace NexoBar.OperationalConfiguration;

internal sealed class PreparationResponsibilityService(
    OperationalConfigurationDbContext dbContext,
    IOperationalConfigurationAuthorization authorization)
{
    private const long CreationLockNamespace = 0x5052455052455300;

    internal async Task<CreatePreparationResponsibilityResult> CreateAsync(
        Guid idempotencyKey,
        CreatePreparationResponsibilityRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OperationalName))
        {
            return CreatePreparationResponsibilityResult.Invalid();
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
            return CreatePreparationResponsibilityResult.AuthenticationRequired();
        }

        var existingCommand = await dbContext.PreparationResponsibilityCreationCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);

        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existingCommand.Matches(actor.IdentityId, operationalName)
                ? CreatePreparationResponsibilityResult.Created(Map(existingCommand))
                : CreatePreparationResponsibilityResult.IdempotencyConflict();
        }

        if (await dbContext.PreparationResponsibilityLifecycleCommands.AnyAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken))
            return CreatePreparationResponsibilityResult.IdempotencyConflict();

        if (!await authorization.StabilizeGeneralConfigurationAsync(
                actor.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return CreatePreparationResponsibilityResult.GeneralConfigurationRequired();
        }

        var responsibility = new PreparationResponsibility(
            Guid.CreateVersion7(), operationalName);
        var response = Map(responsibility);
        dbContext.PreparationResponsibilities.Add(responsibility);
        dbContext.PreparationResponsibilityCreationCommands.Add(
            new PreparationResponsibilityCreationCommand(
                idempotencyKey, actor.IdentityId, operationalName, response));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName:
                    "UX_operational_configuration_responsibilities_normalized_name"
            })
        {
            await transaction.RollbackAsync(cancellationToken);
            return CreatePreparationResponsibilityResult.DuplicateName();
        }

        return CreatePreparationResponsibilityResult.Created(response);
    }

    internal async Task<IReadOnlyList<PreparationResponsibilityResponse>> ListAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var actor = await authorization.StabilizeSessionAsync(
            transaction.GetDbTransaction(),
            cancellationToken);
        if (actor is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PreparationResponsibilityAuthorizationException(
                CreatePreparationResponsibilityOutcome.AuthenticationRequired);
        }

        if (!await authorization.StabilizeGeneralConfigurationAsync(
                actor.IdentityId,
                transaction.GetDbTransaction(),
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PreparationResponsibilityAuthorizationException(
                CreatePreparationResponsibilityOutcome.GeneralConfigurationRequired);
        }

        var responsibilities = await dbContext.PreparationResponsibilities
            .AsNoTracking()
            .OrderBy(responsibility => responsibility.OperationalName)
            .ThenBy(responsibility => responsibility.Id)
            .ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return responsibilities.Select(Map).ToArray();
    }

    private static long CreateTransactionLockKey(Guid idempotencyKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        idempotencyKey.TryWriteBytes(bytes, bigEndian: true, out _);
        return CreationLockNamespace ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^
            BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }

    private static PreparationResponsibilityResponse Map(
        PreparationResponsibility responsibility) =>
        new(responsibility.Id, responsibility.OperationalName, responsibility.IsActive);

    private static PreparationResponsibilityResponse Map(
        PreparationResponsibilityCreationCommand command) =>
        new(command.ResultResponsibilityId, command.ResultOperationalName);
}

internal sealed record CreatePreparationResponsibilityResult(
    CreatePreparationResponsibilityOutcome Outcome,
    PreparationResponsibilityResponse? Responsibility)
{
    internal static CreatePreparationResponsibilityResult Created(
        PreparationResponsibilityResponse responsibility) =>
        new(CreatePreparationResponsibilityOutcome.Created, responsibility);
    internal static CreatePreparationResponsibilityResult Invalid() =>
        new(CreatePreparationResponsibilityOutcome.Invalid, null);
    internal static CreatePreparationResponsibilityResult DuplicateName() =>
        new(CreatePreparationResponsibilityOutcome.DuplicateName, null);
    internal static CreatePreparationResponsibilityResult IdempotencyConflict() =>
        new(CreatePreparationResponsibilityOutcome.IdempotencyConflict, null);
    internal static CreatePreparationResponsibilityResult AuthenticationRequired() =>
        new(CreatePreparationResponsibilityOutcome.AuthenticationRequired, null);
    internal static CreatePreparationResponsibilityResult GeneralConfigurationRequired() =>
        new(CreatePreparationResponsibilityOutcome.GeneralConfigurationRequired, null);
}

internal enum CreatePreparationResponsibilityOutcome
{
    Created,
    Invalid,
    DuplicateName,
    IdempotencyConflict,
    AuthenticationRequired,
    GeneralConfigurationRequired
}

internal sealed class PreparationResponsibilityAuthorizationException(
    CreatePreparationResponsibilityOutcome outcome) : Exception
{
    internal CreatePreparationResponsibilityOutcome Outcome { get; } = outcome;
}
