using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NexoBar.IdentitiesAndCapabilities;
using Npgsql;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class CredentialAndSessionPersistenceTests(
    IdentitiesAndCapabilitiesFixture fixture)
{
    [Theory]
    [InlineData("  Ana.Local  ", "ANA.LOCAL")]
    [InlineData("i", "I")]
    [InlineData("ı", "ı")]
    public void Login_locator_normalization_is_trimmed_and_invariant(
        string input,
        string expected) =>
        Assert.Equal(expected, LoginIdentifierNormalizer.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_login_locator_is_rejected(string loginIdentifier)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.ProvisionCredentialAsync(
                identity.Id,
                loginIdentifier,
                "secret",
                token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_secret_is_rejected_without_trimming_valid_secrets(string secret)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.ProvisionCredentialAsync(
                identity.Id,
                "ana",
                secret,
                token));
    }

    [Fact]
    public async Task Credential_hashes_exact_secret_and_never_persists_raw_secret()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        const string secret = " exact secret ";

        var credential = await fixture.ProvisionCredentialAsync(
            identity.Id,
            "  Ana.Login  ",
            secret,
            token);

        Assert.Equal("Ana.Login", credential.LoginIdentifier);
        Assert.Equal("ANA.LOGIN", credential.NormalizedLoginIdentifier);
        Assert.NotEqual(secret, credential.SecretVerifier);
        var verifier = new PasswordSecretVerifier();
        Assert.Equal(
            SecretVerificationResult.Success,
            verifier.Verify(credential.SecretVerifier, secret));
        Assert.Equal(
            SecretVerificationResult.Failed,
            verifier.Verify(credential.SecretVerifier, secret.Trim()));
    }

    [Fact]
    public void Password_hasher_rehash_needed_result_is_preserved()
    {
        const string secret = "rehash me";
        var oldHasher = new PasswordHasher<object>(Options.Create(
            new PasswordHasherOptions
            {
                CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2
            }));
        var oldVerifier = oldHasher.HashPassword(new object(), secret);

        Assert.Equal(
            SecretVerificationResult.SuccessRehashNeeded,
            new PasswordSecretVerifier().Verify(oldVerifier, secret));
    }

    [Fact]
    public async Task Canonical_locator_and_identity_are_both_unique()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var first = await fixture.CreateIdentityAsync("Alex", true, token);
        var second = await fixture.CreateIdentityAsync("alex", true, token);
        await fixture.ProvisionCredentialAsync(first.Id, "Alex.Login", "one", token);

        var locatorConflict = await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.ProvisionCredentialAsync(second.Id, " alex.login ", "two", token));
        Assert.Equal(
            "UX_local_credential_normalized_login",
            Assert.IsType<PostgresException>(locatorConflict.InnerException).ConstraintName);

        var identityConflict = await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.ProvisionCredentialAsync(first.Id, "another", "three", token));
        Assert.Equal(
            "PK_local_credentials",
            Assert.IsType<PostgresException>(identityConflict.InnerException).ConstraintName);
    }

    [Fact]
    public async Task Credential_requires_an_existing_identity()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.ProvisionCredentialAsync(
                Guid.NewGuid(),
                "orphan",
                "secret",
                token));
        Assert.Equal(
            "FK_local_credential_identity",
            Assert.IsType<PostgresException>(exception.InnerException).ConstraintName);
    }

    [Fact]
    public async Task Session_token_is_256_bit_hashed_unique_and_session_id_is_uuid_v7()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        var generated = SessionToken.Generate();
        var now = fixture.Clock.GetUtcNow();
        var session = new IdentitySession(
            identity.Id,
            generated.TokenHash,
            now,
            now.AddHours(12));
        await fixture.InsertSessionAsync(session, token);

        var persisted = Assert.Single(await fixture.ReadSessionsAsync(token));
        Assert.Equal(32, Microsoft.AspNetCore.WebUtilities.WebEncoders
            .Base64UrlDecode(generated.RawToken).Length);
        Assert.Equal(32, persisted.TokenHash.Length);
        Assert.Equal(generated.TokenHash, persisted.TokenHash);
        Assert.DoesNotContain(generated.RawToken, Convert.ToHexString(persisted.TokenHash));
        Assert.Equal(7, UuidVersion(persisted.Id));

        var duplicate = new IdentitySession(
            identity.Id,
            generated.TokenHash,
            now.AddSeconds(1),
            now.AddHours(12));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.InsertSessionAsync(duplicate, token));
        Assert.Equal(
            "UX_session_token_hash",
            Assert.IsType<PostgresException>(exception.InnerException).ConstraintName);
    }

    [Fact]
    public async Task Multiple_sessions_for_one_identity_are_supported()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        var now = fixture.Clock.GetUtcNow();

        await fixture.InsertSessionAsync(
            new IdentitySession(
                identity.Id,
                SessionToken.Generate().TokenHash,
                now,
                now.AddHours(12)),
            token);
        await fixture.InsertSessionAsync(
            new IdentitySession(
                identity.Id,
                SessionToken.Generate().TokenHash,
                now,
                now.AddHours(12)),
            token);

        Assert.Equal(2, (await fixture.ReadSessionsAsync(token)).Length);
    }

    [Fact]
    public async Task Transactional_stabilizer_locks_and_revalidates_session_and_identity()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var identity = await fixture.CreateIdentityAsync("Ana", true, token);
        var now = fixture.Clock.GetUtcNow();
        var session = new IdentitySession(
            identity.Id,
            SessionToken.Generate().TokenHash,
            now,
            now.AddHours(12));
        await fixture.InsertSessionAsync(session, token);

        await using var connection = await fixture.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var stabilized = await fixture.StabilizeAsync(
            identity.Id,
            session.Id,
            transaction,
            token);

        Assert.Equal(
            new StabilizedAuthenticatedSession(identity.Id, session.Id),
            stabilized);
        await transaction.RollbackAsync(token);
    }

    private static int UuidVersion(Guid value)
    {
        var bytes = value.ToByteArray(bigEndian: true);
        return bytes[6] >> 4;
    }
}
