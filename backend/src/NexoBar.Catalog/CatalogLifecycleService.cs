using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.Catalog;

internal sealed class CatalogLifecycleService(
    CatalogDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    ICatalogConfigurationCapabilityStabilizer catalogConfiguration)
{
    private const long GroupCreationNamespace = 0x47524F5550435245;
    private const long GroupChangeNamespace = 0x47524F5550434847;
    private const long NameChangeNamespace = 0x4E414D4543484745;
    private const long RetireNamespace = 0x5245544952455052;
    private const long ReactivateNamespace = 0x5245414354495650;

    internal async Task<GroupCommandResult> CreateGroupAsync(Guid key, CreateGroupRequest request, CancellationToken token)
    {
        var name = request.OperationalName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return GroupCommandResult.Invalid("operationalName", "An operational name is required.");
        await using var transaction = await BeginLockedAsync(key, GroupCreationNamespace, token);
        var actor = await StabilizeAsync(transaction, token);
        if (actor is null) return GroupCommandResult.AuthenticationRequired();
        var prior = await dbContext.GroupCreationCommands.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, token);
        if (prior is not null)
        {
            await transaction.CommitAsync(token);
            return prior.Matches(actor.Value, name) ? GroupCommandResult.Created(new(prior.ResultGroupId, prior.IntentOperationalName)) : GroupCommandResult.IdempotencyConflict();
        }
        if (!await AuthorizeAsync(actor.Value, transaction, token)) return GroupCommandResult.Forbidden();
        var group = new CatalogGroup(Guid.CreateVersion7(), name);
        dbContext.Groups.Add(group);
        dbContext.GroupCreationCommands.Add(new GroupCreationCommand(key, actor.Value, name, group.Id));
        try { await dbContext.SaveChangesAsync(token); await transaction.CommitAsync(token); }
        catch (DbUpdateException e) when (IsUnique(e, "UX_catalog_groups_normalized_operational_name"))
        { await transaction.RollbackAsync(token); return GroupCommandResult.NameConflict(); }
        return GroupCommandResult.Created(new(group.Id, group.OperationalName));
    }

    internal async Task<GroupListResult> ListGroupsAsync(CancellationToken token)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
        var actor = await StabilizeAsync(transaction, token);
        if (actor is null) return GroupListResult.AuthenticationRequired();
        if (!await AuthorizeAsync(actor.Value, transaction, token)) return GroupListResult.Forbidden();
        var groups = await dbContext.Groups.AsNoTracking().OrderBy(x => x.OperationalName).ThenBy(x => x.Id)
            .Select(x => new GroupResponse(x.Id, x.OperationalName)).ToArrayAsync(token);
        await transaction.CommitAsync(token);
        return GroupListResult.Succeeded(groups);
    }

    internal async Task<ProductGroupCommandResult> ChangeGroupAsync(Guid key, Guid productId, ChangeProductGroupRequest request, CancellationToken token)
    {
        await using var transaction = await BeginLockedAsync(key, GroupChangeNamespace, token);
        var actor = await StabilizeAsync(transaction, token);
        if (actor is null) return ProductGroupCommandResult.AuthenticationRequired();
        var prior = await dbContext.ProductGroupChangeCommands.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, token);
        if (prior is not null)
        {
            await transaction.CommitAsync(token);
            return prior.Matches(actor.Value, productId, request.ExpectedCurrentGroupId, request.NewGroupId)
                ? ProductGroupCommandResult.Changed(new(productId, prior.ResultGroupId)) : ProductGroupCommandResult.IdempotencyConflict();
        }
        if (!await AuthorizeAsync(actor.Value, transaction, token)) return ProductGroupCommandResult.Forbidden();
        if (request.NewGroupId is Guid groupId && !await dbContext.Groups.AnyAsync(x => x.Id == groupId, token))
            return ProductGroupCommandResult.GroupNotFound(groupId);
        var rows = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE catalog.products SET group_id = {request.NewGroupId}
            WHERE id = {productId} AND is_active
              AND group_id IS NOT DISTINCT FROM {request.ExpectedCurrentGroupId}
            """, token);
        if (rows == 0)
        {
            var current = await ReadDiagnosticAsync(productId, transaction, token);
            await transaction.CommitAsync(token);
            return current is null ? ProductGroupCommandResult.NotFound() : !current.IsActive
                ? ProductGroupCommandResult.NotCurrent() : ProductGroupCommandResult.Stale(current.GroupId);
        }
        dbContext.ProductGroupChangeCommands.Add(new(key, actor.Value, productId, request.ExpectedCurrentGroupId, request.NewGroupId));
        await dbContext.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return ProductGroupCommandResult.Changed(new(productId, request.NewGroupId));
    }

    internal async Task<ProductNameCommandResult> ChangeNameAsync(Guid key, Guid productId, ChangeProductOperationalNameRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.NewOperationalName)) return ProductNameCommandResult.Invalid("newOperationalName", "An operational name is required.");
        await using var transaction = await BeginLockedAsync(key, NameChangeNamespace, token);
        var actor = await StabilizeAsync(transaction, token);
        if (actor is null) return ProductNameCommandResult.AuthenticationRequired();
        var prior = await dbContext.ProductOperationalNameChangeCommands.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, token);
        if (prior is not null)
        {
            await transaction.CommitAsync(token);
            return prior.Matches(actor.Value, productId, request.ExpectedCurrentOperationalName, request.NewOperationalName)
                ? ProductNameCommandResult.Changed(new(productId, prior.ResultOperationalName)) : ProductNameCommandResult.IdempotencyConflict();
        }
        if (!await AuthorizeAsync(actor.Value, transaction, token)) return ProductNameCommandResult.Forbidden();
        try
        {
            var rows = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE catalog.products SET operational_name = {request.NewOperationalName}
                WHERE id = {productId} AND operational_name = {request.ExpectedCurrentOperationalName}
                """, token);
            if (rows == 0)
            {
                var current = await ReadDiagnosticAsync(productId, transaction, token);
                await transaction.CommitAsync(token);
                return current is null ? ProductNameCommandResult.NotFound() : ProductNameCommandResult.Stale(current.OperationalName);
            }
            dbContext.ProductOperationalNameChangeCommands.Add(new(key, actor.Value, productId, request.ExpectedCurrentOperationalName, request.NewOperationalName));
            await dbContext.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return ProductNameCommandResult.Changed(new(productId, request.NewOperationalName));
        }
        catch (Exception e) when (IsUnique(e, "UX_catalog_products_active_normalized_operational_name"))
        { await transaction.RollbackAsync(token); return ProductNameCommandResult.NameConflict(); }
    }

    internal async Task<ProductLifecycleCommandResult> RetireAsync(Guid key, Guid productId, CancellationToken token)
    {
        await using var transaction = await BeginLockedAsync(key, RetireNamespace, token);
        var actor = await StabilizeAsync(transaction, token);
        if (actor is null) return ProductLifecycleCommandResult.AuthenticationRequired();
        var prior = await dbContext.ProductRetireCommands.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, token);
        if (prior is not null)
        {
            await transaction.CommitAsync(token);
            return prior.Matches(actor.Value, productId) ? ProductLifecycleCommandResult.Changed(new(productId, prior.ResultIsActive, prior.ResultIsAvailable)) : ProductLifecycleCommandResult.IdempotencyConflict();
        }
        if (!await AuthorizeAsync(actor.Value, transaction, token)) return ProductLifecycleCommandResult.Forbidden();
        var current = await ReadDiagnosticAsync(productId, transaction, token);
        if (current is null) return ProductLifecycleCommandResult.NotFound();
        if (!current.IsActive) return ProductLifecycleCommandResult.AlreadyRetired();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE catalog.products SET is_active = false WHERE id = {productId}", token);
        dbContext.ProductRetireCommands.Add(new(key, actor.Value, productId, current.IsAvailable));
        await dbContext.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return ProductLifecycleCommandResult.Changed(new(productId, false, current.IsAvailable));
    }

    internal async Task<ProductLifecycleCommandResult> ReactivateAsync(Guid key, Guid productId, CancellationToken token)
    {
        await using var transaction = await BeginLockedAsync(key, ReactivateNamespace, token);
        var actor = await StabilizeAsync(transaction, token);
        if (actor is null) return ProductLifecycleCommandResult.AuthenticationRequired();
        var prior = await dbContext.ProductReactivateCommands.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, token);
        if (prior is not null)
        {
            await transaction.CommitAsync(token);
            return prior.Matches(actor.Value, productId) ? ProductLifecycleCommandResult.Changed(new(productId, prior.ResultIsActive, prior.ResultIsAvailable)) : ProductLifecycleCommandResult.IdempotencyConflict();
        }
        if (!await AuthorizeAsync(actor.Value, transaction, token)) return ProductLifecycleCommandResult.Forbidden();
        var current = await ReadDiagnosticAsync(productId, transaction, token);
        if (current is null) return ProductLifecycleCommandResult.NotFound();
        if (current.IsActive) return ProductLifecycleCommandResult.AlreadyActive();
        try
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE catalog.products SET is_active = true, is_available = true WHERE id = {productId}", token);
            dbContext.ProductReactivateCommands.Add(new(key, actor.Value, productId));
            await dbContext.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return ProductLifecycleCommandResult.Changed(new(productId, true, true));
        }
        catch (Exception e) when (IsUnique(e, "UX_catalog_products_active_normalized_operational_name"))
        { await transaction.RollbackAsync(token); return ProductLifecycleCommandResult.NameConflict(); }
    }

    private async Task<IDbContextTransaction> BeginLockedAsync(Guid key, long scope, CancellationToken token)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(token);
        var value = LockKey(key, scope);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({value})", token);
        return transaction;
    }

    private async Task<Guid?> StabilizeAsync(IDbContextTransaction transaction, CancellationToken token) =>
        (await sessionStabilizer.StabilizeAsync(transaction.GetDbTransaction(), token))?.IdentityId;

    private async Task<bool> AuthorizeAsync(Guid actor, IDbContextTransaction transaction, CancellationToken token)
    {
        if (await catalogConfiguration.StabilizeResponsibilityAsync(actor, transaction.GetDbTransaction(), token)) return true;
        await transaction.RollbackAsync(token); return false;
    }

    private async Task<ProductLifecycleDiagnostic?> ReadDiagnosticAsync(Guid id, IDbContextTransaction transaction, CancellationToken token) =>
        await dbContext.Database.SqlQuery<ProductLifecycleDiagnostic>($"""
            SELECT is_active AS "IsActive", is_available AS "IsAvailable", group_id AS "GroupId", operational_name AS "OperationalName"
            FROM catalog.products WHERE id = {id} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);

    private static bool IsUnique(Exception e, string constraint) => e switch
    {
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: var name } => name == constraint,
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: var name } } => name == constraint,
        _ => false
    };
    private static long LockKey(Guid key, long scope)
    {
        Span<byte> bytes = stackalloc byte[16]; key.TryWriteBytes(bytes, bigEndian: true, out _);
        return scope ^ BinaryPrimitives.ReadInt64BigEndian(bytes[..8]) ^ BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }
}

internal sealed record ProductLifecycleDiagnostic(bool IsActive, bool IsAvailable, Guid? GroupId, string OperationalName);
internal enum CatalogMutationOutcome { Changed, Created, Invalid, NameConflict, GroupNotFound, NotFound, NotCurrent, Stale, AlreadyRetired, AlreadyActive, IdempotencyConflict, AuthenticationRequired, Forbidden }
internal sealed record GroupCommandResult(CatalogMutationOutcome Outcome, GroupResponse? Group = null, string? Field = null, string? Error = null)
{ internal static GroupCommandResult Created(GroupResponse x) => new(CatalogMutationOutcome.Created, x); internal static GroupCommandResult Invalid(string f, string e) => new(CatalogMutationOutcome.Invalid, null, f, e); internal static GroupCommandResult NameConflict() => new(CatalogMutationOutcome.NameConflict); internal static GroupCommandResult IdempotencyConflict() => new(CatalogMutationOutcome.IdempotencyConflict); internal static GroupCommandResult AuthenticationRequired() => new(CatalogMutationOutcome.AuthenticationRequired); internal static GroupCommandResult Forbidden() => new(CatalogMutationOutcome.Forbidden); }
internal sealed record GroupListResult(CatalogMutationOutcome Outcome, IReadOnlyList<GroupResponse>? Groups = null)
{ internal static GroupListResult Succeeded(IReadOnlyList<GroupResponse> x) => new(CatalogMutationOutcome.Changed, x); internal static GroupListResult AuthenticationRequired() => new(CatalogMutationOutcome.AuthenticationRequired); internal static GroupListResult Forbidden() => new(CatalogMutationOutcome.Forbidden); }
internal sealed record ProductGroupCommandResult(CatalogMutationOutcome Outcome, ProductGroupResponse? Result = null, Guid? GroupId = null)
{ internal static ProductGroupCommandResult Changed(ProductGroupResponse x) => new(CatalogMutationOutcome.Changed, x); internal static ProductGroupCommandResult GroupNotFound(Guid x) => new(CatalogMutationOutcome.GroupNotFound, null, x); internal static ProductGroupCommandResult NotFound() => new(CatalogMutationOutcome.NotFound); internal static ProductGroupCommandResult NotCurrent() => new(CatalogMutationOutcome.NotCurrent); internal static ProductGroupCommandResult Stale(Guid? x) => new(CatalogMutationOutcome.Stale, null, x); internal static ProductGroupCommandResult IdempotencyConflict() => new(CatalogMutationOutcome.IdempotencyConflict); internal static ProductGroupCommandResult AuthenticationRequired() => new(CatalogMutationOutcome.AuthenticationRequired); internal static ProductGroupCommandResult Forbidden() => new(CatalogMutationOutcome.Forbidden); }
internal sealed record ProductNameCommandResult(CatalogMutationOutcome Outcome, ProductOperationalNameResponse? Result = null, string? CurrentName = null, string? Field = null, string? Error = null)
{ internal static ProductNameCommandResult Changed(ProductOperationalNameResponse x) => new(CatalogMutationOutcome.Changed, x); internal static ProductNameCommandResult Invalid(string f, string e) => new(CatalogMutationOutcome.Invalid, null, null, f, e); internal static ProductNameCommandResult NameConflict() => new(CatalogMutationOutcome.NameConflict); internal static ProductNameCommandResult NotFound() => new(CatalogMutationOutcome.NotFound); internal static ProductNameCommandResult Stale(string x) => new(CatalogMutationOutcome.Stale, null, x); internal static ProductNameCommandResult IdempotencyConflict() => new(CatalogMutationOutcome.IdempotencyConflict); internal static ProductNameCommandResult AuthenticationRequired() => new(CatalogMutationOutcome.AuthenticationRequired); internal static ProductNameCommandResult Forbidden() => new(CatalogMutationOutcome.Forbidden); }
internal sealed record ProductLifecycleCommandResult(CatalogMutationOutcome Outcome, ProductLifecycleResponse? Result = null)
{ internal static ProductLifecycleCommandResult Changed(ProductLifecycleResponse x) => new(CatalogMutationOutcome.Changed, x); internal static ProductLifecycleCommandResult NameConflict() => new(CatalogMutationOutcome.NameConflict); internal static ProductLifecycleCommandResult NotFound() => new(CatalogMutationOutcome.NotFound); internal static ProductLifecycleCommandResult AlreadyRetired() => new(CatalogMutationOutcome.AlreadyRetired); internal static ProductLifecycleCommandResult AlreadyActive() => new(CatalogMutationOutcome.AlreadyActive); internal static ProductLifecycleCommandResult IdempotencyConflict() => new(CatalogMutationOutcome.IdempotencyConflict); internal static ProductLifecycleCommandResult AuthenticationRequired() => new(CatalogMutationOutcome.AuthenticationRequired); internal static ProductLifecycleCommandResult Forbidden() => new(CatalogMutationOutcome.Forbidden); }
