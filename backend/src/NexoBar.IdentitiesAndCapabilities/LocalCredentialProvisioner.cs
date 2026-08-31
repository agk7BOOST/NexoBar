namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class LocalCredentialProvisioner(
    IdentitiesAndCapabilitiesDbContext dbContext,
    ISecretVerifier secretVerifier)
{
    internal async Task<LocalCredential> ProvisionAsync(
        Guid identityId,
        string loginIdentifier,
        string secret,
        CancellationToken cancellationToken)
    {
        var trimmedLoginIdentifier = LoginIdentifierNormalizer.Trim(loginIdentifier)
            ?? throw new ArgumentException(
                "Login identifier must contain non-whitespace text.",
                nameof(loginIdentifier));
        var normalizedLoginIdentifier =
            LoginIdentifierNormalizer.Normalize(trimmedLoginIdentifier)!;

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException(
                "Secret must contain non-whitespace text.",
                nameof(secret));
        }

        var credential = new LocalCredential(
            identityId,
            trimmedLoginIdentifier,
            normalizedLoginIdentifier,
            secretVerifier.Hash(secret));
        dbContext.LocalCredentials.Add(credential);
        await dbContext.SaveChangesAsync(cancellationToken);
        return credential;
    }
}
