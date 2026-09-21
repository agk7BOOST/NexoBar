namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class ExtraordinaryGeneralConfigurationRecoveryCommand
{
    private ExtraordinaryGeneralConfigurationRecoveryCommand()
    {
    }

    internal ExtraordinaryGeneralConfigurationRecoveryCommand(
        Guid commandId,
        Guid targetIdentityId,
        ExtraordinaryRecoveryLoginIntentMode loginIntentMode,
        string? requestedLoginIdentifier,
        string retryCredentialVerifier,
        int recoveryFactorGeneration,
        DateTimeOffset completedAt)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("Command identifier is required.", nameof(commandId));
        }

        if (targetIdentityId == Guid.Empty)
        {
            throw new ArgumentException("Target Identity identifier is required.", nameof(targetIdentityId));
        }

        if (!Enum.IsDefined(loginIntentMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(loginIntentMode),
                "Login intent mode is invalid.");
        }

        var canonicalRequestedLoginIdentifier = LoginIdentifierNormalizer.Normalize(
            requestedLoginIdentifier);
        if ((loginIntentMode == ExtraordinaryRecoveryLoginIntentMode.PreserveExisting &&
                requestedLoginIdentifier is not null) ||
            (loginIntentMode == ExtraordinaryRecoveryLoginIntentMode.ExplicitIdentifier &&
                canonicalRequestedLoginIdentifier is null))
        {
            throw new ArgumentException(
                "Requested login identifier is inconsistent with login intent mode.",
                nameof(requestedLoginIdentifier));
        }

        if (string.IsNullOrWhiteSpace(retryCredentialVerifier))
        {
            throw new ArgumentException(
                "Retry credential verifier is required.",
                nameof(retryCredentialVerifier));
        }

        if (recoveryFactorGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoveryFactorGeneration),
                "Recovery factor generation must be positive.");
        }

        if (completedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Completion time must be UTC.",
                nameof(completedAt));
        }

        CommandId = commandId;
        TargetIdentityId = targetIdentityId;
        LoginIntentMode = loginIntentMode;
        RequestedLoginIdentifier = canonicalRequestedLoginIdentifier;
        RetryCredentialVerifier = retryCredentialVerifier;
        RecoveryFactorGeneration = recoveryFactorGeneration;
        CompletedAt = completedAt;
    }

    internal Guid CommandId { get; private set; }

    internal Guid TargetIdentityId { get; private set; }

    internal ExtraordinaryRecoveryLoginIntentMode LoginIntentMode { get; private set; }

    internal string? RequestedLoginIdentifier { get; private set; }

    internal string RetryCredentialVerifier { get; private set; } = string.Empty;

    internal int RecoveryFactorGeneration { get; private set; }

    internal DateTimeOffset CompletedAt { get; private set; }
}

public enum ExtraordinaryRecoveryLoginIntentMode
{
    PreserveExisting,
    ExplicitIdentifier
}
