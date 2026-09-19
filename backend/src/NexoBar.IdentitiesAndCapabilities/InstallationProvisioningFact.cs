namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class InstallationProvisioningFact
{
    internal const short SingletonKey = 1;

    private InstallationProvisioningFact()
    {
    }

    internal InstallationProvisioningFact(
        Guid provisioningCommandId,
        Guid initialIdentityId,
        DateTimeOffset completedAt,
        byte[] retryIntentFingerprint,
        string retrySecretVerifier)
    {
        if (provisioningCommandId == Guid.Empty)
        {
            throw new ArgumentException(
                "Provisioning command identifier is required.",
                nameof(provisioningCommandId));
        }

        if (initialIdentityId == Guid.Empty)
        {
            throw new ArgumentException(
                "Initial Identity identifier is required.",
                nameof(initialIdentityId));
        }

        if (completedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Completion time must be UTC.",
                nameof(completedAt));
        }

        if (retryIntentFingerprint.Length == 0)
        {
            throw new ArgumentException(
                "Retry intent fingerprint is required.",
                nameof(retryIntentFingerprint));
        }

        if (string.IsNullOrWhiteSpace(retrySecretVerifier))
        {
            throw new ArgumentException(
                "Retry secret verifier is required.",
                nameof(retrySecretVerifier));
        }

        Key = SingletonKey;
        Origin = InstallationProvisioningOrigin.InitialProvisioning;
        ProvisioningCommandId = provisioningCommandId;
        InitialIdentityId = initialIdentityId;
        CompletedAt = completedAt;
        RetryIntentFingerprint = retryIntentFingerprint.ToArray();
        RetrySecretVerifier = retrySecretVerifier;
    }

    internal short Key { get; private set; }

    internal InstallationProvisioningOrigin Origin { get; private set; }

    internal DateTimeOffset? CompletedAt { get; private set; }

    internal Guid? ProvisioningCommandId { get; private set; }

    internal Guid? InitialIdentityId { get; private set; }

    internal byte[]? RetryIntentFingerprint { get; private set; }

    internal string? RetrySecretVerifier { get; private set; }
}

internal enum InstallationProvisioningOrigin
{
    InitialProvisioning,
    LegacyBackfill
}
