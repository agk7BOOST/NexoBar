using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace NexoBar.OperationalConfiguration;

internal sealed class PreparationResponsibilityLifecycleCommand
{
    private PreparationResponsibilityLifecycleCommand() { }
    internal PreparationResponsibilityLifecycleCommand(Guid key, Guid actor, string kind, Guid target,
        string expectedName, bool expectedActive, string? newName, ConfigurationLifecycleResponse result)
    {
        IdempotencyKey = key;
        ActorIdentityId = actor;
        CommandKind = kind;
        TargetId = target;
        ExpectedOperationalName = expectedName;
        ExpectedIsActive = expectedActive;
        NewOperationalName = newName;
        ResultOperationalName = result.OperationalName;
        ResultIsActive = result.IsActive;
        ResultIsDeleted = result.IsDeleted;
    }
    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = string.Empty;
    internal Guid TargetId { get; private set; }
    internal string ExpectedOperationalName { get; private set; } = string.Empty;
    internal bool ExpectedIsActive { get; private set; }
    internal string? NewOperationalName { get; private set; }
    internal string ResultOperationalName { get; private set; } = string.Empty;
    internal bool ResultIsActive { get; private set; }
    internal bool ResultIsDeleted { get; private set; }
    internal bool Matches(Guid actor, string kind, Guid target, string expectedName, bool expectedActive, string? newName) =>
        ActorIdentityId == actor && CommandKind == kind && TargetId == target &&
        ExpectedOperationalName == expectedName && ExpectedIsActive == expectedActive && NewOperationalName == newName;
    internal ConfigurationLifecycleResponse ToResponse() => new(TargetId, ResultOperationalName, ResultIsActive, ResultIsDeleted);
}

internal sealed class PreparationResponsibilityLifecycleService(
    OperationalConfigurationDbContext db,
    IOperationalConfigurationAuthorization authorization,
    IDestinationProductReferences products, IDestinationOperationalParticipation participation, IDestinationEnablementReferences enablements)
{
    internal async Task<ConfigurationMutationResult> ExecuteAsync(Guid key, Guid id, string kind,
        ConfigurationLifecycleRequest request, string? newName, CancellationToken token)
    {
        newName = newName?.Trim();
        if (string.IsNullOrWhiteSpace(request.ExpectedCurrentOperationalName) ||
            (kind == "Rename" && string.IsNullOrWhiteSpace(newName)))
            return new(400, "operational_name_invalid", "Ingresá un nombre operacional válido.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        Span<byte> bytes = stackalloc byte[16];
        key.TryWriteBytes(bytes, bigEndian: true, out _);
        var lockKey = 0x5052455052455300 ^ BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^ BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", token);
        var actor = await authorization.StabilizeSessionAsync(transaction.GetDbTransaction(), token);
        if (actor is null) return new(401, "invalid_session", "La sesión no está vigente.");
        var prior = await db.PreparationResponsibilityLifecycleCommands.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, token);
        if (prior is not null)
        {
            await transaction.CommitAsync(token);
            return prior.Matches(actor.IdentityId, kind, id, request.ExpectedCurrentOperationalName, request.ExpectedIsActive, newName)
                ? new(200, Result: prior.ToResponse())
                : new(409, "idempotency_key_conflict", "La clave identifica otra intención o actor.");
        }
        if (await db.PreparationResponsibilityCreationCommands.AnyAsync(x => x.IdempotencyKey == key, token))
            return new(409, "idempotency_key_conflict", "La clave identifica una creación anterior.");
        if (!await authorization.StabilizeGeneralConfigurationAsync(actor.IdentityId, transaction.GetDbTransaction(), token))
            return new(403, "general_configuration_required", "Se requiere Configuración general.");
        var current = await db.PreparationResponsibilities.FromSqlInterpolated($"SELECT * FROM operational_configuration.preparation_responsibilities WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(token);
        if (current is null) return new(404, "not_found", "El registro no existe.");
        if (current.OperationalName != request.ExpectedCurrentOperationalName || current.IsActive != request.ExpectedIsActive)
            return new(409, "concurrency_conflict", "El nombre o estado cambió. Actualizá la lista antes de continuar.");
        if (kind == "Retire" && !current.IsActive) return new(409, "already_retired", "El registro ya está retirado.");
        if (kind == "Reactivate" && current.IsActive) return new(409, "already_active", "El registro ya está activo.");
        if (kind == "Retire" && await products.HasProductReferencesAsync(id, true, transaction.GetDbTransaction(), token))
            return new(409, "retire.active_products", "No se puede retirar porque todavía hay productos activos configurados para este destino. Cambiá primero su configuración de preparación.");
        if (kind == "Delete")
        {
            if (await products.HasProductReferencesAsync(id, false, transaction.GetDbTransaction(), token))
                return new(409, "delete.product_references", "Hay productos activos o retirados configurados para este destino.");
            if (await enablements.HasEnablementsAsync(id, transaction.GetDbTransaction(), token))
                return new(409, "delete.preparation_enablements", "Hay habilitaciones de preparación que conservan este destino.");
            if (await participation.HasParticipationAsync(id, transaction.GetDbTransaction(), token))
                return new(409, "delete.operational_participation", "Existe Trabajo o uso operacional histórico que conserva este destino.");
        }
        switch (kind)
        {
            case "Rename": current.Rename(newName!); break;
            case "Retire": current.Retire(); break;
            case "Reactivate": current.Reactivate(); break;
            case "Delete": db.PreparationResponsibilities.Remove(current); break;
            default: throw new InvalidOperationException("Unknown configuration command.");
        }
        var response = new ConfigurationLifecycleResponse(id, current.OperationalName, current.IsActive, kind == "Delete");
        db.PreparationResponsibilityLifecycleCommands.Add(new(key, actor.IdentityId, kind, id,
            request.ExpectedCurrentOperationalName, request.ExpectedIsActive, newName, response));
        try
        {
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_operational_configuration_responsibilities_normalized_name" })
        {
            await transaction.RollbackAsync(token);
            return new(409, "operational_name_conflict", "Ya existe un registro con ese nombre, incluso entre retirados.");
        }
        return new(200, Result: response);
    }
}
