namespace NexoBar.OperationalConfiguration;

internal sealed class PreparationResponsibilityCreationCommand
{
    private PreparationResponsibilityCreationCommand() { }

    internal PreparationResponsibilityCreationCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        string intentOperationalName,
        PreparationResponsibilityResponse result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = PreparationResponsibilityCommandKind.Create;
        IntentOperationalName = intentOperationalName;
        ResultResponsibilityId = result.Id;
        ResultOperationalName = result.OperationalName;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid? ActorIdentityId { get; private set; }
    internal PreparationResponsibilityCommandKind CommandKind { get; private set; }
    internal string IntentOperationalName { get; private set; } = string.Empty;
    internal Guid ResultResponsibilityId { get; private set; }
    internal string ResultOperationalName { get; private set; } = string.Empty;

    internal bool Matches(Guid actorIdentityId, string operationalName) =>
        ActorIdentityId == actorIdentityId &&
        CommandKind == PreparationResponsibilityCommandKind.Create &&
        string.Equals(IntentOperationalName, operationalName, StringComparison.Ordinal);
}

internal enum PreparationResponsibilityCommandKind
{
    Create
}
