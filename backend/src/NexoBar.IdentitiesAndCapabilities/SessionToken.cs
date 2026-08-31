using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace NexoBar.IdentitiesAndCapabilities;

internal static class SessionToken
{
    internal const int TokenSizeBytes = 32;
    internal const int TokenHashSizeBytes = 32;

    internal static GeneratedSessionToken Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenSizeBytes);
        return new GeneratedSessionToken(
            WebEncoders.Base64UrlEncode(bytes),
            SHA256.HashData(bytes));
    }

    internal static bool TryHash(string token, out byte[] tokenHash)
    {
        tokenHash = [];
        byte[] bytes;
        try
        {
            bytes = WebEncoders.Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != TokenSizeBytes)
        {
            return false;
        }

        tokenHash = SHA256.HashData(bytes);
        return true;
    }
}

internal sealed record GeneratedSessionToken(string RawToken, byte[] TokenHash);
