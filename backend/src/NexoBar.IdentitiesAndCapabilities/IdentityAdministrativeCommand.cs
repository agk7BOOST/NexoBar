namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class IdentityAdministrativeCommand
{
    private IdentityAdministrativeCommand()
    {
    }

    internal IdentityAdministrativeCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        AdministrativeCommandKind commandKind,
        byte[] intentFingerprint,
        string? intentSecretVerifier,
        string resultPayload)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = commandKind;
        IntentFingerprint = intentFingerprint.ToArray();
        IntentSecretVerifier = intentSecretVerifier;
        ResultPayload = resultPayload;
    }

    internal Guid IdempotencyKey { get; private set; }

    internal Guid ActorIdentityId { get; private set; }

    internal AdministrativeCommandKind CommandKind { get; private set; }

    internal byte[] IntentFingerprint { get; private set; } = [];

    internal string? IntentSecretVerifier { get; private set; }

    internal string ResultPayload { get; private set; } = string.Empty;

    internal bool Matches(
        Guid actorIdentityId,
        AdministrativeCommandKind commandKind,
        byte[] intentFingerprint) =>
        ActorIdentityId == actorIdentityId &&
        CommandKind == commandKind &&
        IntentFingerprint.SequenceEqual(intentFingerprint);
}

internal enum AdministrativeCommandKind
{
    CreateIdentity,
    ChangeOperationalName,
    ActivateIdentity,
    DeactivateIdentity,
    SetLocalCredential,
    AssignResponsibility,
    RevokeResponsibility,
    GrantPreparationEnablement,
    RevokePreparationEnablement
}
