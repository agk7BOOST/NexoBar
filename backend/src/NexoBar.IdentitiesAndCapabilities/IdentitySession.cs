namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class IdentitySession
{
    private IdentitySession()
    {
    }

    internal IdentitySession(
        Guid identityId,
        byte[] tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset absoluteExpiresAt)
    {
        if (identityId == Guid.Empty)
        {
            throw new ArgumentException("Identity identifier is required.", nameof(identityId));
        }

        if (tokenHash is not { Length: SessionToken.TokenHashSizeBytes })
        {
            throw new ArgumentException("Session token hash must contain 32 bytes.", nameof(tokenHash));
        }

        if (absoluteExpiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(absoluteExpiresAt),
                "Absolute expiration must be after session creation.");
        }

        Id = Guid.CreateVersion7();
        IdentityId = identityId;
        TokenHash = tokenHash.ToArray();
        CreatedAt = createdAt;
        LastActivityAt = createdAt;
        AbsoluteExpiresAt = absoluteExpiresAt;
    }

    internal Guid Id { get; private set; }

    internal Guid IdentityId { get; private set; }

    internal byte[] TokenHash { get; private set; } = [];

    internal DateTimeOffset CreatedAt { get; private set; }

    internal DateTimeOffset LastActivityAt { get; private set; }

    internal DateTimeOffset AbsoluteExpiresAt { get; private set; }

    internal DateTimeOffset? RevokedAt { get; private set; }
}
