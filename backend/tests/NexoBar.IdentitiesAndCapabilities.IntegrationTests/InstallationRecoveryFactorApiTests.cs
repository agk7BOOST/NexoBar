using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class InstallationRecoveryFactorApiTests(
    IdentitiesAndCapabilitiesFixture fixture)
{
    [Fact]
    public async Task Provisioned_legacy_installation_establishes_generation_one_with_separate_verifiers()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await InsertFactAsync(token);
        var (client, actor) = await CreateAdministratorAsync("establish-admin", token);
        using (client)
        {
            var key = Guid.NewGuid();
            var factor = Factor(1);
            using var response = await RotateAsync(client, factor, key, token);

            response.EnsureSuccessStatusCode();
            var result = await ReadResultAsync(response, token);
            Assert.Equal(1, result.Generation);
            Assert.Equal(fixture.Clock.GetUtcNow(), result.EstablishedAt);
            Assert.Null(result.LastRotatedAt);
            var state = Assert.IsType<InstallationRecoveryStateSnapshot>(
                await fixture.ReadInstallationRecoveryStateAsync(token));
            var command = Assert.IsType<AdministrativeCommandSnapshot>(
                await fixture.ReadAdministrativeCommandAsync(key, token));
            Assert.Equal(actor.Id, command.ActorIdentityId);
            Assert.Equal(AdministrativeCommandKind.RotateInstallationRecoveryFactor,
                command.CommandKind);
            Assert.NotEqual(state.RecoveryFactorVerifier, command.IntentSecretVerifier);
            Assert.True(RecoveryFactor.TryParse(factor, out var parsed));
            var verifier = new PasswordRecoveryFactorVerifier();
            Assert.NotEqual(SecretVerificationResult.Failed,
                verifier.Verify(state.RecoveryFactorVerifier, parsed!));
            Assert.NotEqual(SecretVerificationResult.Failed,
                verifier.Verify(command.IntentSecretVerifier!, parsed!));
            Assert.DoesNotContain(factor, command.ResultPayload, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Rotation_and_historical_replays_preserve_current_state()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await InsertFactAsync(token);
        var (client, _) = await CreateAdministratorAsync("rotate-admin", token);
        using (client)
        {
            var firstKey = Guid.NewGuid();
            using var first = await RotateAsync(client, Factor(1), firstKey, token);
            first.EnsureSuccessStatusCode();
            var firstResult = await ReadResultAsync(first, token);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            var secondKey = Guid.NewGuid();
            using var second = await RotateAsync(client, Factor(2), secondKey, token);
            second.EnsureSuccessStatusCode();
            var secondResult = await ReadResultAsync(second, token);
            Assert.Equal(2, secondResult.Generation);
            Assert.Equal(firstResult.EstablishedAt, secondResult.EstablishedAt);
            Assert.NotNull(secondResult.LastRotatedAt);

            using var firstReplay = await RotateAsync(client, Factor(1), firstKey, token);
            firstReplay.EnsureSuccessStatusCode();
            Assert.Equal(firstResult, await ReadResultAsync(firstReplay, token));
            Assert.Equal(2, (await fixture.ReadInstallationRecoveryStateAsync(token))!.Generation);
            using var changed = await RotateAsync(client, Factor(3), secondKey, token);
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        }
    }

    [Fact]
    public async Task Security_and_idempotency_rules_are_enforced()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await InsertFactAsync(token);
        using var anonymous = fixture.CreateClient();
        using var anonymousResponse = await anonymous.PostAsJsonAsync(
            "/api/installation-recovery-factor/rotate",
            new { newRecoveryFactor = Factor(1) }, token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var identity = await fixture.CreateIdentityAsync("No GC", true, token);
        await fixture.ProvisionCredentialAsync(identity.Id, "no-gc", "secret", token);
        using var forbiddenClient = fixture.CreateClient();
        await LoginAsync(forbiddenClient, "no-gc", "secret", token);
        using var forbidden = await RotateAsync(forbiddenClient, Factor(1), Guid.NewGuid(), token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var (first, _) = await CreateAdministratorAsync("idempotent-first", token);
        var (second, _) = await CreateAdministratorAsync("idempotent-second", token);
        using (first)
        using (second)
        {
            var key = Guid.NewGuid();
            using var committed = await RotateAsync(first, Factor(1), key, token);
            committed.EnsureSuccessStatusCode();
            using var otherActor = await RotateAsync(second, Factor(1), key, token);
            Assert.Equal(HttpStatusCode.Conflict, otherActor.StatusCode);
            using var invalid = await RotateAsync(first, "not-a-factor", Guid.NewGuid(), token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    [Fact]
    public async Task Missing_fact_fails_closed_and_replay_survives_general_configuration_revocation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (client, actor) = await CreateAdministratorAsync("replay-admin", token);
        using (client)
        {
            using var inconsistent = await RotateAsync(client, Factor(1), Guid.NewGuid(), token);
            Assert.Equal(HttpStatusCode.InternalServerError, inconsistent.StatusCode);
            Assert.Equal(0, await fixture.CountInstallationRecoveryStatesAsync(token));
            Assert.Equal(0, await fixture.CountAdministrativeCommandsAsync(token));

            await InsertFactAsync(token);
            var key = Guid.NewGuid();
            using var committed = await RotateAsync(client, Factor(1), key, token);
            committed.EnsureSuccessStatusCode();
            await RevokeGeneralConfigurationAsync(actor.Id, token);

            using var replay = await RotateAsync(client, Factor(1), key, token);
            replay.EnsureSuccessStatusCode();
            using var fresh = await RotateAsync(client, Factor(2), Guid.NewGuid(), token);
            Assert.Equal(HttpStatusCode.Forbidden, fresh.StatusCode);
        }
    }

    [Fact]
    public async Task Concurrent_rotations_advance_successive_generations()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await InsertFactAsync(token);
        var (client, _) = await CreateAdministratorAsync("concurrent-admin", token);
        using (client)
        {
            using var establish = await RotateAsync(client, Factor(1), Guid.NewGuid(), token);
            establish.EnsureSuccessStatusCode();
            var responses = await Task.WhenAll(
                RotateAsync(client, Factor(2), Guid.NewGuid(), token),
                RotateAsync(client, Factor(3), Guid.NewGuid(), token));
            using (responses[0])
            using (responses[1])
            {
                Assert.All(responses, response => response.EnsureSuccessStatusCode());
            }

            Assert.Equal(3, (await fixture.ReadInstallationRecoveryStateAsync(token))!.Generation);
        }
    }

    private async Task InsertFactAsync(CancellationToken cancellationToken) =>
        await fixture.InsertInstallationProvisioningFactAsync(new InstallationProvisioningFact(
            Guid.NewGuid(), Guid.NewGuid(), fixture.Clock.GetUtcNow(), [1], "credential", "recovery"),
            cancellationToken);

    private async Task RevokeGeneralConfigurationAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM identities_and_capabilities.responsibility_assignments " +
            "WHERE identity_id = @identityId AND responsibility_code = 'GeneralConfiguration'";
        command.Parameters.AddWithValue("identityId", identityId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(HttpClient Client, IdentitySnapshot Identity)> CreateAdministratorAsync(
        string loginIdentifier,
        CancellationToken cancellationToken)
    {
        var identity = await fixture.CreateIdentityAsync(loginIdentifier, true, cancellationToken);
        await fixture.ProvisionCredentialAsync(identity.Id, loginIdentifier, "secret", cancellationToken);
        await fixture.InsertAssignmentAsync(
            identity.Id, FunctionalResponsibility.GeneralConfiguration, cancellationToken);
        var client = fixture.CreateClient();
        await LoginAsync(client, loginIdentifier, "secret", cancellationToken);
        return (client, identity);
    }

    private static async Task<HttpResponseMessage> RotateAsync(
        HttpClient client, string factor, Guid key, CancellationToken cancellationToken)
    {
        var token = await AntiforgeryAsync(client, cancellationToken);
        var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/installation-recovery-factor/rotate")
        {
            Content = JsonContent.Create(new { newRecoveryFactor = factor })
        };
        request.Headers.Add("X-NexoBar-CSRF", token);
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task LoginAsync(
        HttpClient client, string loginIdentifier, string secret, CancellationToken cancellationToken)
    {
        var token = await AntiforgeryAsync(client, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity-sessions")
        {
            Content = JsonContent.Create(new { loginIdentifier, secret })
        };
        request.Headers.Add("X-NexoBar-CSRF", token);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> AntiforgeryAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/api/security/antiforgery", cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("requestToken").GetString()!;
    }

    private static async Task<InstallationRecoveryFactorResponse> ReadResultAsync(
        HttpResponseMessage response, CancellationToken cancellationToken) =>
        Assert.IsType<InstallationRecoveryFactorResponse>(
            await response.Content.ReadFromJsonAsync<InstallationRecoveryFactorResponse>(
                cancellationToken: cancellationToken));

    private static string Factor(int seed) =>
        Convert.ToBase64String(Enumerable.Range(seed, 32).Select(value => (byte)value).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
