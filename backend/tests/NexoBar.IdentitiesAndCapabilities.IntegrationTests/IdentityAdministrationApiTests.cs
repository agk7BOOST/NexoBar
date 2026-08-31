using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

[Collection(IdentitiesAndCapabilitiesCollection.Name)]
public sealed class IdentityAdministrationApiTests(
    IdentitiesAndCapabilitiesFixture fixture)
{
    [Fact]
    public async Task Administration_requires_session_and_current_general_configuration()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        using var anonymous = fixture.CreateClient();

        using var anonymousResponse = await anonymous.GetAsync(
            "/api/identities",
            token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var actor = await fixture.CreateIdentityAsync("Actor", true, token);
        await fixture.ProvisionCredentialAsync(actor.Id, "actor", "secret", token);
        using var authenticated = fixture.CreateClient();
        await LoginAsync(authenticated, "actor", "secret", token);

        using var forbidden = await authenticated.GetAsync("/api/identities", token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(
            "identities_and_capabilities.general_configuration_required",
            await ReadProblemCodeAsync(forbidden, token));

        await fixture.InsertAssignmentAsync(
            actor.Id,
            FunctionalResponsibility.GeneralConfiguration,
            token);
        using var allowed = await authenticated.GetAsync("/api/identities", token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        using var revoke = await SendCommandAsync(
            authenticated,
            $"/api/identities/{actor.Id:D}/responsibilities/GeneralConfiguration/revoke",
            new { },
            token);
        Assert.Equal(HttpStatusCode.Conflict, revoke.StatusCode);
    }

    [Fact]
    public async Task Create_change_name_and_list_expose_only_administrative_state()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (client, _) = await CreateAdministratorAsync("admin-create", token);
        using (client)
        {
            using var create = await SendCommandAsync(
                client,
                "/api/identities",
                new { operationalName = "  Nueva Persona  " },
                token);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var created = await ReadIdentityAsync(create, token);
            Assert.Equal("Nueva Persona", created.OperationalName);
            Assert.False(created.IsActive);
            Assert.Null(created.LoginIdentifier);

            using var rename = await SendCommandAsync(
                client,
                $"/api/identities/{created.IdentityId:D}/change-operational-name",
                new { operationalName = " Nombre Operativo " },
                token);
            Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

            using var list = await client.GetAsync("/api/identities", token);
            list.EnsureSuccessStatusCode();
            var json = await list.Content.ReadAsStringAsync(token);
            Assert.Contains("Nombre Operativo", json, StringComparison.Ordinal);
            Assert.Contains("loginIdentifier", json, StringComparison.Ordinal);
            Assert.DoesNotContain("normalizedLoginIdentifier", json, StringComparison.Ordinal);
            Assert.DoesNotContain("secretVerifier", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tokenHash", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sessionId", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Revoked_general_configuration_forbids_next_new_command_but_replay_survives()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (firstClient, first) = await CreateAdministratorAsync("first-admin", token);
        var (secondClient, _) = await CreateAdministratorAsync("second-admin", token);
        using (firstClient)
        using (secondClient)
        {
            var key = Guid.NewGuid();
            using var original = await SendCommandAsync(
                firstClient,
                "/api/identities",
                new { operationalName = "Replay Result", isActive = true },
                token,
                key);
            original.EnsureSuccessStatusCode();
            var originalJson = await original.Content.ReadAsStringAsync(token);

            using var revoke = await SendCommandAsync(
                secondClient,
                $"/api/identities/{first.Id:D}/responsibilities/GeneralConfiguration/revoke",
                new { },
                token);
            revoke.EnsureSuccessStatusCode();

            using var replay = await SendCommandAsync(
                firstClient,
                "/api/identities",
                new { operationalName = "Replay Result", isActive = true },
                token,
                key);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(originalJson, await replay.Content.ReadAsStringAsync(token));

            using var next = await SendCommandAsync(
                firstClient,
                "/api/identities",
                new { operationalName = "Forbidden" },
                token);
            Assert.Equal(HttpStatusCode.Forbidden, next.StatusCode);
        }
    }

    [Fact]
    public async Task Deactivated_actor_is_unauthenticated_on_the_next_command()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (firstClient, first) = await CreateAdministratorAsync("inactive-actor", token);
        var (secondClient, _) = await CreateAdministratorAsync("active-actor", token);
        using (firstClient)
        using (secondClient)
        {
            using var deactivate = await SendCommandAsync(
                secondClient,
                $"/api/identities/{first.Id:D}/deactivate",
                new { },
                token);
            deactivate.EnsureSuccessStatusCode();

            using var next = await firstClient.GetAsync("/api/identities", token);
            Assert.Equal(HttpStatusCode.Unauthorized, next.StatusCode);
            Assert.Equal(
                "identities_and_capabilities.invalid_session",
                await ReadProblemCodeAsync(next, token));
        }
    }

    [Fact]
    public async Task Last_operational_general_configuration_path_cannot_be_revoked_or_deactivated()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (client, actor) = await CreateAdministratorAsync("only-admin", token);
        using (client)
        {
            using var revoke = await SendCommandAsync(
                client,
                $"/api/identities/{actor.Id:D}/responsibilities/GeneralConfiguration/revoke",
                new { },
                token);
            await AssertLastPathAsync(revoke, token);

            using var deactivate = await SendCommandAsync(
                client,
                $"/api/identities/{actor.Id:D}/deactivate",
                new { },
                token);
            await AssertLastPathAsync(deactivate, token);

            Assert.True(Assert.Single(await fixture.ReadIdentitiesAsync(token)).IsActive);
            Assert.Contains(
                FunctionalResponsibility.GeneralConfiguration,
                await fixture.ReadResponsibilitiesAsync(actor.Id, token));
        }
    }

    [Fact]
    public async Task Inactive_or_credentialless_assignment_is_not_an_operational_path()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (client, actor) = await CreateAdministratorAsync("valid-admin", token);
        var withoutCredential = await fixture.CreateIdentityAsync(
            "Without Credential",
            true,
            token);
        await fixture.InsertAssignmentAsync(
            withoutCredential.Id,
            FunctionalResponsibility.GeneralConfiguration,
            token);
        var inactive = await fixture.CreateIdentityAsync("Inactive", false, token);
        await fixture.ProvisionCredentialAsync(inactive.Id, "inactive", "secret", token);
        await fixture.InsertAssignmentAsync(
            inactive.Id,
            FunctionalResponsibility.GeneralConfiguration,
            token);

        using (client)
        {
            using var removeOnlyValid = await SendCommandAsync(
                client,
                $"/api/identities/{actor.Id:D}/responsibilities/GeneralConfiguration/revoke",
                new { },
                token);
            await AssertLastPathAsync(removeOnlyValid, token);

            using var revokeCredentialless = await SendCommandAsync(
                client,
                $"/api/identities/{withoutCredential.Id:D}/responsibilities/GeneralConfiguration/revoke",
                new { },
                token);
            revokeCredentialless.EnsureSuccessStatusCode();

            using var revokeInactive = await SendCommandAsync(
                client,
                $"/api/identities/{inactive.Id:D}/responsibilities/GeneralConfiguration/revoke",
                new { },
                token);
            revokeInactive.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Two_paths_allow_one_deactivation_and_revoke_only_target_sessions()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (actorClient, actor) = await CreateAdministratorAsync("actor-deactivate", token);
        var target = await fixture.CreateIdentityAsync("Target", true, token);
        await fixture.ProvisionCredentialAsync(target.Id, "target", "secret", token);
        await fixture.InsertAssignmentAsync(
            target.Id,
            FunctionalResponsibility.GeneralConfiguration,
            token);
        using var targetFirst = fixture.CreateClient();
        using var targetSecond = fixture.CreateClient();
        await LoginAsync(targetFirst, "target", "secret", token);
        await LoginAsync(targetSecond, "target", "secret", token);

        using (actorClient)
        {
            using var deactivate = await SendCommandAsync(
                actorClient,
                $"/api/identities/{target.Id:D}/deactivate",
                new { },
                token);
            deactivate.EnsureSuccessStatusCode();

            var sessions = await fixture.ReadSessionsAsync(token);
            Assert.All(
                sessions.Where(session => session.IdentityId == target.Id),
                session => Assert.NotNull(session.RevokedAt));
            Assert.All(
                sessions.Where(session => session.IdentityId == actor.Id),
                session => Assert.Null(session.RevokedAt));

            using var actorCurrent = await actorClient.GetAsync(
                "/api/identity-sessions/current",
                token);
            Assert.Equal(HttpStatusCode.OK, actorCurrent.StatusCode);
            using var targetCurrent = await targetFirst.GetAsync(
                "/api/identity-sessions/current",
                token);
            Assert.Equal(HttpStatusCode.Unauthorized, targetCurrent.StatusCode);
        }
    }

    [Fact]
    public async Task Credential_replace_is_atomic_revokes_only_target_and_replay_does_not_repeat_it()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (adminClient, admin) = await CreateAdministratorAsync("credential-admin", token);
        var target = await fixture.CreateIdentityAsync("Credential Target", true, token);
        await fixture.ProvisionCredentialAsync(target.Id, "old-login", "old-secret", token);
        using var oldTarget = fixture.CreateClient();
        await LoginAsync(oldTarget, "old-login", "old-secret", token);
        var key = Guid.NewGuid();

        using (adminClient)
        {
            using var replace = await SendCommandAsync(
                adminClient,
                $"/api/identities/{target.Id:D}/credential",
                new { loginIdentifier = "new-login", secret = "new-secret" },
                token,
                key);
            replace.EnsureSuccessStatusCode();

            Assert.All(
                (await fixture.ReadSessionsAsync(token)).Where(
                    session => session.IdentityId == target.Id),
                session => Assert.NotNull(session.RevokedAt));
            Assert.All(
                (await fixture.ReadSessionsAsync(token)).Where(
                    session => session.IdentityId == admin.Id),
                session => Assert.Null(session.RevokedAt));

            using var newTarget = fixture.CreateClient();
            await LoginAsync(newTarget, "new-login", "new-secret", token);
            using var replay = await SendCommandAsync(
                adminClient,
                $"/api/identities/{target.Id:D}/credential",
                new { loginIdentifier = "new-login", secret = "new-secret" },
                token,
                key);
            replay.EnsureSuccessStatusCode();
            using var changedSecret = await SendCommandAsync(
                adminClient,
                $"/api/identities/{target.Id:D}/credential",
                new { loginIdentifier = "new-login", secret = "different-secret" },
                token,
                key);
            Assert.Equal(HttpStatusCode.Conflict, changedSecret.StatusCode);
            using var stillCurrent = await newTarget.GetAsync(
                "/api/identity-sessions/current",
                token);
            Assert.Equal(HttpStatusCode.OK, stillCurrent.StatusCode);
        }
    }

    [Fact]
    public async Task Self_credential_replace_completes_then_revokes_current_session()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (client, actor) = await CreateAdministratorAsync("self-credential", token);
        using (client)
        {
            using var replace = await SendCommandAsync(
                client,
                $"/api/identities/{actor.Id:D}/credential",
                new { secret = "replacement" },
                token);
            Assert.Equal(HttpStatusCode.OK, replace.StatusCode);

            using var current = await client.GetAsync(
                "/api/identity-sessions/current",
                token);
            Assert.Equal(HttpStatusCode.Unauthorized, current.StatusCode);

            using var relogin = fixture.CreateClient();
            await LoginAsync(relogin, "self-credential", "replacement", token);
        }
    }

    [Fact]
    public async Task Duplicate_login_failure_rolls_back_credential_and_session_revocation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (client, actor) = await CreateAdministratorAsync("unique-admin", token);
        var other = await fixture.CreateIdentityAsync("Other", true, token);
        await fixture.ProvisionCredentialAsync(other.Id, "occupied", "secret", token);
        var original = await fixture.ReadCredentialAsync(actor.Id, token);

        using (client)
        {
            using var conflict = await SendCommandAsync(
                client,
                $"/api/identities/{actor.Id:D}/credential",
                new { loginIdentifier = "occupied", secret = "replacement" },
                token);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal(
                "identities_and_capabilities.login_identifier_conflict",
                await ReadProblemCodeAsync(conflict, token));
            Assert.Equal(original, await fixture.ReadCredentialAsync(actor.Id, token));

            using var current = await client.GetAsync(
                "/api/identity-sessions/current",
                token);
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        }
    }

    [Fact]
    public async Task Responsibilities_and_enablements_remain_independent_and_deterministic()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (client, _) = await CreateAdministratorAsync("capability-admin", token);
        var target = await fixture.CreateIdentityAsync("Capability Target", true, token);
        var preparationResponsibilityId =
            await fixture.CreatePreparationResponsibilityAsync("Kitchen", token);

        using (client)
        {
            using var grantWithoutAssignment = await SendCommandAsync(
                client,
                $"/api/identities/{target.Id:D}/preparation-enablement/{preparationResponsibilityId:D}/grant",
                new { },
                token);
            grantWithoutAssignment.EnsureSuccessStatusCode();

            using var assign = await SendCommandAsync(
                client,
                $"/api/identities/{target.Id:D}/responsibilities/Preparation/assign",
                new { },
                token);
            assign.EnsureSuccessStatusCode();
            using var duplicate = await SendCommandAsync(
                client,
                $"/api/identities/{target.Id:D}/responsibilities/Preparation/assign",
                new { },
                token);
            duplicate.EnsureSuccessStatusCode();
            Assert.Single(await fixture.ReadResponsibilitiesAsync(target.Id, token));

            using var revokeResponsibility = await SendCommandAsync(
                client,
                $"/api/identities/{target.Id:D}/responsibilities/Preparation/revoke",
                new { },
                token);
            revokeResponsibility.EnsureSuccessStatusCode();
            Assert.Equal(
                new[] { preparationResponsibilityId },
                await fixture.ReadEnablementsAsync(target.Id, token));

            using var revokeEnablement = await SendCommandAsync(
                client,
                $"/api/identities/{target.Id:D}/preparation-enablement/{preparationResponsibilityId:D}/revoke",
                new { },
                token);
            revokeEnablement.EnsureSuccessStatusCode();
            Assert.Empty(await fixture.ReadEnablementsAsync(target.Id, token));

            using var invalidCode = await SendCommandAsync(
                client,
                $"/api/identities/{target.Id:D}/responsibilities/Arbitrary/assign",
                new { },
                token);
            Assert.Equal(HttpStatusCode.BadRequest, invalidCode.StatusCode);

            using var missingResponsibility = await SendCommandAsync(
                client,
                $"/api/identities/{target.Id:D}/preparation-enablement/{Guid.NewGuid():D}/grant",
                new { },
                token);
            Assert.Equal(HttpStatusCode.NotFound, missingResponsibility.StatusCode);
        }
    }

    [Fact]
    public async Task Every_command_family_has_durable_same_intention_replay()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (client, _) = await CreateAdministratorAsync("family-admin", token);
        var preparationResponsibilityId =
            await fixture.CreatePreparationResponsibilityAsync("Family Kitchen", token);
        using (client)
        {
            var createKey = Guid.NewGuid();
            using var create = await SendCommandAsync(
                client,
                "/api/identities",
                new { operationalName = "Family Target" },
                token,
                createKey);
            create.EnsureSuccessStatusCode();
            var target = await ReadIdentityAsync(create, token);
            using var createReplay = await SendCommandAsync(
                client,
                "/api/identities",
                new { operationalName = "Family Target" },
                token,
                createKey);
            createReplay.EnsureSuccessStatusCode();

            await AssertCommandReplaysAsync(
                client,
                $"/api/identities/{target.IdentityId:D}/change-operational-name",
                new { operationalName = "Renamed Target" },
                token);
            var sessionsBeforeActivation = await fixture.ReadSessionsAsync(token);
            await AssertCommandReplaysAsync(
                client,
                $"/api/identities/{target.IdentityId:D}/activate",
                new { },
                token);
            Assert.Equal(
                sessionsBeforeActivation.Length,
                (await fixture.ReadSessionsAsync(token)).Length);

            var credentialKey = Guid.NewGuid();
            using var credential = await SendCommandAsync(
                client,
                $"/api/identities/{target.IdentityId:D}/credential",
                new { loginIdentifier = "family-target", secret = "secret" },
                token,
                credentialKey);
            credential.EnsureSuccessStatusCode();
            var verifier = (await fixture.ReadCredentialAsync(
                target.IdentityId,
                token))!.SecretVerifier;
            using var credentialReplay = await SendCommandAsync(
                client,
                $"/api/identities/{target.IdentityId:D}/credential",
                new { loginIdentifier = "family-target", secret = "secret" },
                token,
                credentialKey);
            credentialReplay.EnsureSuccessStatusCode();
            Assert.Equal(
                verifier,
                (await fixture.ReadCredentialAsync(
                    target.IdentityId,
                    token))!.SecretVerifier);

            await AssertCommandReplaysAsync(
                client,
                $"/api/identities/{target.IdentityId:D}/responsibilities/Preparation/assign",
                new { },
                token);
            await AssertCommandReplaysAsync(
                client,
                $"/api/identities/{target.IdentityId:D}/responsibilities/Preparation/revoke",
                new { },
                token);
            await AssertCommandReplaysAsync(
                client,
                $"/api/identities/{target.IdentityId:D}/preparation-enablement/{preparationResponsibilityId:D}/grant",
                new { },
                token);
            await AssertCommandReplaysAsync(
                client,
                $"/api/identities/{target.IdentityId:D}/preparation-enablement/{preparationResponsibilityId:D}/revoke",
                new { },
                token);
            await AssertCommandReplaysAsync(
                client,
                $"/api/identities/{target.IdentityId:D}/deactivate",
                new { },
                token);

            Assert.Equal(9, await fixture.CountAdministrativeCommandsAsync(token));
            Assert.Single(
                await fixture.ReadIdentitiesAsync(token),
                identity => identity.Id == target.IdentityId);
            Assert.Empty(await fixture.ReadResponsibilitiesAsync(target.IdentityId, token));
            Assert.Empty(await fixture.ReadEnablementsAsync(target.IdentityId, token));
        }
    }

    [Fact]
    public async Task Idempotency_binds_key_to_actor_and_intention()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (firstClient, _) = await CreateAdministratorAsync("idem-first", token);
        var (secondClient, _) = await CreateAdministratorAsync("idem-second", token);
        var key = Guid.NewGuid();
        using (firstClient)
        using (secondClient)
        {
            using var first = await SendCommandAsync(
                firstClient,
                "/api/identities",
                new { operationalName = "Idempotent" },
                token,
                key);
            first.EnsureSuccessStatusCode();
            var firstIdentity = await ReadIdentityAsync(first, token);

            using var replay = await SendCommandAsync(
                firstClient,
                "/api/identities",
                new { operationalName = "Idempotent" },
                token,
                key);
            replay.EnsureSuccessStatusCode();
            var replayedIdentity = await ReadIdentityAsync(replay, token);
            Assert.Equal(firstIdentity.IdentityId, replayedIdentity.IdentityId);
            Assert.Equal(firstIdentity.OperationalName, replayedIdentity.OperationalName);
            Assert.Equal(firstIdentity.IsActive, replayedIdentity.IsActive);
            Assert.Equal(firstIdentity.LoginIdentifier, replayedIdentity.LoginIdentifier);
            Assert.Equal(
                firstIdentity.Responsibilities,
                replayedIdentity.Responsibilities);
            Assert.Equal(
                firstIdentity.PreparationEnablements,
                replayedIdentity.PreparationEnablements);

            using var changedIntent = await SendCommandAsync(
                firstClient,
                "/api/identities",
                new { operationalName = "Different" },
                token,
                key);
            Assert.Equal(HttpStatusCode.Conflict, changedIntent.StatusCode);

            using var changedActor = await SendCommandAsync(
                secondClient,
                "/api/identities",
                new { operationalName = "Idempotent" },
                token,
                key);
            Assert.Equal(HttpStatusCode.Conflict, changedActor.StatusCode);
            Assert.Equal(1, await fixture.CountAdministrativeCommandsAsync(token));
            Assert.Equal(
                3,
                (await fixture.ReadIdentitiesAsync(token)).Length);
        }
    }

    [Fact]
    public async Task Concurrent_cross_revoke_cannot_remove_both_paths()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (firstClient, first) = await CreateAdministratorAsync("race-first", token);
        var (secondClient, second) = await CreateAdministratorAsync("race-second", token);
        using (firstClient)
        using (secondClient)
        {
            var firstRequest = SendCommandAsync(
                firstClient,
                $"/api/identities/{second.Id:D}/responsibilities/GeneralConfiguration/revoke",
                new { },
                token);
            var secondRequest = SendCommandAsync(
                secondClient,
                $"/api/identities/{first.Id:D}/responsibilities/GeneralConfiguration/revoke",
                new { },
                token);
            var responses = await Task.WhenAll(firstRequest, secondRequest);
            using (responses[0])
            using (responses[1])
            {
                Assert.Contains(responses, response => response.IsSuccessStatusCode);
                Assert.Contains(
                    responses,
                    response => response.StatusCode is HttpStatusCode.Forbidden or
                        HttpStatusCode.Conflict);
            }

            var remaining = (await fixture.ReadResponsibilitiesAsync(first.Id, token))
                .Concat(await fixture.ReadResponsibilitiesAsync(second.Id, token))
                .Count(value =>
                    value == FunctionalResponsibility.GeneralConfiguration);
            Assert.Equal(1, remaining);
        }
    }

    [Fact]
    public async Task Concurrent_deactivate_and_revoke_cannot_remove_both_paths()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var (firstClient, first) = await CreateAdministratorAsync("mixed-first", token);
        var (secondClient, second) = await CreateAdministratorAsync("mixed-second", token);
        using (firstClient)
        using (secondClient)
        {
            var deactivate = SendCommandAsync(
                firstClient,
                $"/api/identities/{second.Id:D}/deactivate",
                new { },
                token);
            var revoke = SendCommandAsync(
                secondClient,
                $"/api/identities/{first.Id:D}/responsibilities/GeneralConfiguration/revoke",
                new { },
                token);
            var responses = await Task.WhenAll(deactivate, revoke);
            using (responses[0])
            using (responses[1])
            {
                Assert.Contains(responses, response => response.IsSuccessStatusCode);
            }

            var identities = await fixture.ReadIdentitiesAsync(token);
            var firstIsPath = identities.Single(value => value.Id == first.Id).IsActive &&
                (await fixture.ReadResponsibilitiesAsync(first.Id, token)).Contains(
                    FunctionalResponsibility.GeneralConfiguration);
            var secondIsPath = identities.Single(value => value.Id == second.Id).IsActive &&
                (await fixture.ReadResponsibilitiesAsync(second.Id, token)).Contains(
                    FunctionalResponsibility.GeneralConfiguration);
            Assert.True(firstIsPath || secondIsPath);
        }
    }

    private async Task<(HttpClient Client, IdentitySnapshot Identity)>
        CreateAdministratorAsync(
            string loginIdentifier,
            CancellationToken cancellationToken)
    {
        var identity = await fixture.CreateIdentityAsync(
            loginIdentifier,
            true,
            cancellationToken);
        await fixture.ProvisionCredentialAsync(
            identity.Id,
            loginIdentifier,
            "secret",
            cancellationToken);
        await fixture.InsertAssignmentAsync(
            identity.Id,
            FunctionalResponsibility.GeneralConfiguration,
            cancellationToken);
        var client = fixture.CreateClient();
        await LoginAsync(client, loginIdentifier, "secret", cancellationToken);
        return (client, identity);
    }

    private static async Task LoginAsync(
        HttpClient client,
        string loginIdentifier,
        string secret,
        CancellationToken cancellationToken)
    {
        var requestToken = await GetAntiforgeryTokenAsync(client, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity-sessions")
        {
            Content = JsonContent.Create(new { loginIdentifier, secret })
        };
        request.Headers.Add("X-NexoBar-CSRF", requestToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpResponseMessage> SendCommandAsync(
        HttpClient client,
        string path,
        object body,
        CancellationToken cancellationToken,
        Guid? idempotencyKey = null)
    {
        var requestToken = await GetAntiforgeryTokenAsync(client, cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-NexoBar-CSRF", requestToken);
        request.Headers.Add(
            "Idempotency-Key",
            (idempotencyKey ?? Guid.NewGuid()).ToString("D"));
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task AssertCommandReplaysAsync(
        HttpClient client,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        var key = Guid.NewGuid();
        using var first = await SendCommandAsync(
            client,
            path,
            body,
            cancellationToken,
            key);
        first.EnsureSuccessStatusCode();
        var firstJson = await first.Content.ReadAsStringAsync(cancellationToken);
        using var replay = await SendCommandAsync(
            client,
            path,
            body,
            cancellationToken,
            key);
        replay.EnsureSuccessStatusCode();
        Assert.Equal(firstJson, await replay.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task<string> GetAntiforgeryTokenAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            "/api/security/antiforgery",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("requestToken").GetString()!;
    }

    private static async Task<IdentityAdministrationResponse> ReadIdentityAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        Assert.IsType<IdentityAdministrationResponse>(
            await response.Content.ReadFromJsonAsync<IdentityAdministrationResponse>(
                cancellationToken: cancellationToken));

    private static async Task<string> ReadProblemCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task AssertLastPathAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "identities_and_capabilities.last_general_configuration_path",
            await ReadProblemCodeAsync(response, cancellationToken));
    }
}
