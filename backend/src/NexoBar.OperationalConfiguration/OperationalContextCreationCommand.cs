namespace NexoBar.OperationalConfiguration;

internal sealed class OperationalContextCreationCommand
{
    private OperationalContextCreationCommand() { }

    internal OperationalContextCreationCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        string intentOperationalName,
        OperationalContextReference result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        IntentOperationalName = intentOperationalName;
        ResultContextId = result.Id;
        ResultOperationalName = result.OperationalName;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string IntentOperationalName { get; private set; } = string.Empty;
    internal Guid ResultContextId { get; private set; }
    internal string ResultOperationalName { get; private set; } = string.Empty;

    internal bool Matches(Guid actorIdentityId, string operationalName) =>
        ActorIdentityId == actorIdentityId &&
        string.Equals(IntentOperationalName, operationalName, StringComparison.Ordinal);
}
