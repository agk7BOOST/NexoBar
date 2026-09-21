namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class InstallationRecoveryState
{
    internal const short SingletonKey = 1;

    private InstallationRecoveryState()
    {
    }

    internal InstallationRecoveryState(
        string recoveryFactorVerifier,
        int generation,
        DateTimeOffset establishedAt,
        DateTimeOffset? lastRotatedAt = null)
    {
        if (string.IsNullOrWhiteSpace(recoveryFactorVerifier))
        {
            throw new ArgumentException(
                "Recovery factor verifier is required.",
                nameof(recoveryFactorVerifier));
        }

        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "Recovery factor generation must be positive.");
        }

        if (establishedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Establishment time must be UTC.",
                nameof(establishedAt));
        }

        if (lastRotatedAt is { } rotatedAt &&
            (rotatedAt.Offset != TimeSpan.Zero || rotatedAt < establishedAt))
        {
            throw new ArgumentException(
                "Last rotation time must be UTC and cannot precede establishment.",
                nameof(lastRotatedAt));
        }

        Key = SingletonKey;
        RecoveryFactorVerifier = recoveryFactorVerifier;
        Generation = generation;
        EstablishedAt = establishedAt;
        LastRotatedAt = lastRotatedAt;
    }

    internal short Key { get; private set; }

    internal string RecoveryFactorVerifier { get; private set; } = string.Empty;

    internal int Generation { get; private set; }

    internal DateTimeOffset EstablishedAt { get; private set; }

    internal DateTimeOffset? LastRotatedAt { get; private set; }

    internal void Rotate(
        string recoveryFactorVerifier,
        int generation,
        DateTimeOffset rotatedAt)
    {
        if (string.IsNullOrWhiteSpace(recoveryFactorVerifier))
        {
            throw new ArgumentException(
                "Recovery factor verifier is required.",
                nameof(recoveryFactorVerifier));
        }

        if (generation <= Generation)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "Recovery factor generation must advance.");
        }

        if (rotatedAt.Offset != TimeSpan.Zero || rotatedAt < EstablishedAt)
        {
            throw new ArgumentException(
                "Rotation time must be UTC and cannot precede establishment.",
                nameof(rotatedAt));
        }

        RecoveryFactorVerifier = recoveryFactorVerifier;
        Generation = generation;
        LastRotatedAt = rotatedAt;
    }
}
