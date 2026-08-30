namespace NexoBar.OperationalConfiguration;

internal sealed class PreparationResponsibilityCreationCommand
{
    private PreparationResponsibilityCreationCommand() { }

    internal PreparationResponsibilityCreationCommand(
        Guid idempotencyKey,
        string intentOperationalName,
        PreparationResponsibilityResponse result)
    {
        IdempotencyKey = idempotencyKey;
        IntentOperationalName = intentOperationalName;
        ResultResponsibilityId = result.Id;
        ResultOperationalName = result.OperationalName;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal string IntentOperationalName { get; private set; } = string.Empty;
    internal Guid ResultResponsibilityId { get; private set; }
    internal string ResultOperationalName { get; private set; } = string.Empty;

    internal bool Matches(string operationalName) =>
        string.Equals(IntentOperationalName, operationalName, StringComparison.Ordinal);
}
