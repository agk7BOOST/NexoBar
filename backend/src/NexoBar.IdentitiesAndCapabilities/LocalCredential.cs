namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class LocalCredential
{
    private LocalCredential()
    {
    }

    internal LocalCredential(
        Guid identityId,
        string loginIdentifier,
        string normalizedLoginIdentifier,
        string secretVerifier)
    {
        if (identityId == Guid.Empty)
        {
            throw new ArgumentException("Identity identifier is required.", nameof(identityId));
        }

        if (string.IsNullOrWhiteSpace(loginIdentifier) ||
            !string.Equals(loginIdentifier, loginIdentifier.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Login identifier must contain trimmed non-whitespace text.",
                nameof(loginIdentifier));
        }

        if (string.IsNullOrWhiteSpace(normalizedLoginIdentifier))
        {
            throw new ArgumentException(
                "Normalized login identifier is required.",
                nameof(normalizedLoginIdentifier));
        }

        if (string.IsNullOrWhiteSpace(secretVerifier))
        {
            throw new ArgumentException("Secret verifier is required.", nameof(secretVerifier));
        }

        IdentityId = identityId;
        LoginIdentifier = loginIdentifier;
        NormalizedLoginIdentifier = normalizedLoginIdentifier;
        SecretVerifier = secretVerifier;
    }

    internal Guid IdentityId { get; private set; }

    internal string LoginIdentifier { get; private set; } = string.Empty;

    internal string NormalizedLoginIdentifier { get; private set; } = string.Empty;

    internal string SecretVerifier { get; private set; } = string.Empty;

    internal void ReplaceSecretVerifier(string secretVerifier)
    {
        if (string.IsNullOrWhiteSpace(secretVerifier))
        {
            throw new ArgumentException("Secret verifier is required.", nameof(secretVerifier));
        }

        SecretVerifier = secretVerifier;
    }

    internal void Replace(
        string loginIdentifier,
        string normalizedLoginIdentifier,
        string secretVerifier)
    {
        if (string.IsNullOrWhiteSpace(loginIdentifier) ||
            !string.Equals(loginIdentifier, loginIdentifier.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Login identifier must contain trimmed non-whitespace text.",
                nameof(loginIdentifier));
        }

        if (string.IsNullOrWhiteSpace(normalizedLoginIdentifier))
        {
            throw new ArgumentException(
                "Normalized login identifier is required.",
                nameof(normalizedLoginIdentifier));
        }

        if (string.IsNullOrWhiteSpace(secretVerifier))
        {
            throw new ArgumentException("Secret verifier is required.", nameof(secretVerifier));
        }

        LoginIdentifier = loginIdentifier;
        NormalizedLoginIdentifier = normalizedLoginIdentifier;
        SecretVerifier = secretVerifier;
    }
}
