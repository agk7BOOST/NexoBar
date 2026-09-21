using Microsoft.AspNetCore.Identity;

namespace NexoBar.IdentitiesAndCapabilities;

public static class RecoveryFactorFormat
{
    public static bool IsCanonical(string? representation) =>
        RecoveryFactor.TryParse(representation, out _);
}

internal sealed class RecoveryFactor
{
    private RecoveryFactor(string representation) => Representation = representation;

    internal string Representation { get; }

    internal static bool TryParse(string? representation, out RecoveryFactor? factor)
    {
        factor = null;
        if (representation is null || representation.Length != 43 ||
            representation.Any(character => !IsBase64UrlCharacter(character)))
        {
            return false;
        }

        try
        {
            var base64 = representation.Replace('-', '+').Replace('_', '/') + "=";
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length != 32 || !string.Equals(
                    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                    representation,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        factor = new RecoveryFactor(representation);
        return true;
    }

    private static bool IsBase64UrlCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
}

internal interface IRecoveryFactorVerifier
{
    string Hash(RecoveryFactor factor);

    SecretVerificationResult Verify(string verifier, RecoveryFactor factor);
}

internal sealed class PasswordRecoveryFactorVerifier : IRecoveryFactorVerifier
{
    private readonly PasswordHasher<RecoveryFactorHashSubject> passwordHasher = new();
    private readonly RecoveryFactorHashSubject subject = new();

    public string Hash(RecoveryFactor factor) =>
        passwordHasher.HashPassword(subject, factor.Representation);

    public SecretVerificationResult Verify(string verifier, RecoveryFactor factor) =>
        passwordHasher.VerifyHashedPassword(subject, verifier, factor.Representation) switch
        {
            PasswordVerificationResult.Success => SecretVerificationResult.Success,
            PasswordVerificationResult.SuccessRehashNeeded =>
                SecretVerificationResult.SuccessRehashNeeded,
            _ => SecretVerificationResult.Failed
        };

    private sealed class RecoveryFactorHashSubject;
}
