using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NexoBar.OperationalConfiguration;

internal sealed class PreparationResponsibilityService(
    OperationalConfigurationDbContext dbContext)
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

        var existingCommand = await dbContext.PreparationResponsibilityCreationCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken);

        if (existingCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existingCommand.Matches(operationalName)
                ? CreatePreparationResponsibilityResult.Created(Map(existingCommand))
                : CreatePreparationResponsibilityResult.IdempotencyConflict();
        }

        var responsibility = new PreparationResponsibility(
            Guid.CreateVersion7(), operationalName);
        var response = Map(responsibility);
        dbContext.PreparationResponsibilities.Add(responsibility);
        dbContext.PreparationResponsibilityCreationCommands.Add(
            new PreparationResponsibilityCreationCommand(
                idempotencyKey, operationalName, response));

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
        var responsibilities = await dbContext.PreparationResponsibilities
            .AsNoTracking()
            .OrderBy(responsibility => responsibility.OperationalName)
            .ThenBy(responsibility => responsibility.Id)
            .ToArrayAsync(cancellationToken);
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
        new(responsibility.Id, responsibility.OperationalName);

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
}

internal enum CreatePreparationResponsibilityOutcome
{
    Created,
    Invalid,
    DuplicateName,
    IdempotencyConflict
}
