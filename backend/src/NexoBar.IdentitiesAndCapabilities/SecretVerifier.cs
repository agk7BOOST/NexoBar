using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace NexoBar.IdentitiesAndCapabilities;

internal interface ISecretVerifier
{
    string Hash(string secret);

    SecretVerificationResult Verify(string verifier, string secret);

    void VerifyDummy(string secret);
}

internal enum SecretVerificationResult
{
    Failed,
    Success,
    SuccessRehashNeeded
}

internal sealed class PasswordSecretVerifier : ISecretVerifier
{
    private readonly PasswordHasher<CredentialHashSubject> passwordHasher = new();
    private readonly CredentialHashSubject subject = new();
    private readonly string dummyVerifier;

    public PasswordSecretVerifier()
    {
        dummyVerifier = passwordHasher.HashPassword(
            subject,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public string Hash(string secret) => passwordHasher.HashPassword(subject, secret);

    public SecretVerificationResult Verify(string verifier, string secret) =>
        passwordHasher.VerifyHashedPassword(subject, verifier, secret) switch
        {
            PasswordVerificationResult.Success => SecretVerificationResult.Success,
            PasswordVerificationResult.SuccessRehashNeeded =>
                SecretVerificationResult.SuccessRehashNeeded,
            _ => SecretVerificationResult.Failed
        };

    public void VerifyDummy(string secret) =>
        _ = passwordHasher.VerifyHashedPassword(subject, dummyVerifier, secret);

    private sealed class CredentialHashSubject;
}
