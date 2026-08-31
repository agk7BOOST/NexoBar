using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoBar.OperationalConfiguration;

namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class CurrentPreparationDestinationQueryService(
    IdentitiesAndCapabilitiesDbContext dbContext,
    IAuthenticatedSessionStabilizer sessionStabilizer,
    IPreparationResponsibilityLookup preparationResponsibilities)
{
    internal async Task<CurrentPreparationDestinationsResult> ListAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var dbTransaction = transaction.GetDbTransaction();
        var session = await sessionStabilizer.StabilizeAsync(
            dbTransaction,
            cancellationToken);
        if (session is null)
        {
            return CurrentPreparationDestinationsResult.Unauthenticated();
        }

        if (!await PreparationAuthorization.HasPreparationResponsibilityAsync(
                session.IdentityId,
                dbTransaction,
                cancellationToken))
        {
            return CurrentPreparationDestinationsResult.Forbidden();
        }

        var enabledIds = await ReadEnabledIdsAsync(
            session.IdentityId,
            dbTransaction,
            cancellationToken);
        if (enabledIds.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return CurrentPreparationDestinationsResult.Succeeded([]);
        }

        var references = await preparationResponsibilities.ReadByIdsAsync(
            enabledIds,
            dbTransaction,
            cancellationToken);
        var byId = references.ToDictionary(reference => reference.Id);
        if (byId.Count != enabledIds.Length ||
            enabledIds.Any(id => !byId.ContainsKey(id)))
        {
            return CurrentPreparationDestinationsResult.ReferenceInconsistent();
        }

        var destinations = enabledIds
            .Select(id => new CurrentPreparationDestinationResponse(
                id,
                byId[id].OperationalName))
            .OrderBy(destination => destination.OperationalName, StringComparer.Ordinal)
            .ThenBy(destination => destination.PreparationResponsibilityId)
            .ToArray();
        await transaction.CommitAsync(cancellationToken);
        return CurrentPreparationDestinationsResult.Succeeded(destinations);
    }

    private static async Task<Guid[]> ReadEnabledIdsAsync(
        Guid identityId,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = PreparationAuthorization.CreateCommand(transaction);
        command.CommandText =
            """
            SELECT preparation_responsibility_id
            FROM identities_and_capabilities.preparation_enablements
            WHERE identity_id = @identity_id
            ORDER BY preparation_responsibility_id
            FOR SHARE
            """;
        PreparationAuthorization.AddParameter(command, "identity_id", identityId);

        var result = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetGuid(0));
        }

        return result.ToArray();
    }
}

internal sealed record CurrentPreparationDestinationResponse(
    Guid PreparationResponsibilityId,
    string OperationalName);

internal sealed record CurrentPreparationDestinationsResult(
    CurrentPreparationDestinationsOutcome Outcome,
    IReadOnlyList<CurrentPreparationDestinationResponse>? Destinations)
{
    internal static CurrentPreparationDestinationsResult Succeeded(
        IReadOnlyList<CurrentPreparationDestinationResponse> destinations) =>
        new(CurrentPreparationDestinationsOutcome.Succeeded, destinations);

    internal static CurrentPreparationDestinationsResult Unauthenticated() =>
        new(CurrentPreparationDestinationsOutcome.Unauthenticated, null);

    internal static CurrentPreparationDestinationsResult Forbidden() =>
        new(CurrentPreparationDestinationsOutcome.Forbidden, null);

    internal static CurrentPreparationDestinationsResult ReferenceInconsistent() =>
        new(CurrentPreparationDestinationsOutcome.ReferenceInconsistent, null);
}

internal enum CurrentPreparationDestinationsOutcome
{
    Succeeded,
    Unauthenticated,
    Forbidden,
    ReferenceInconsistent
}
