using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using NexoBar.Host;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

public sealed class HostInitialProvisioningCommandTests
{
    [Fact]
    public void Provisioning_subcommand_selects_provisioning_mode()
    {
        var selection = HostCommandLine.Parse(
        [
            HostCommandLine.ProvisionInitialAdminCommand,
            "--operational-name", "Initial",
            "--login-identifier", "initial",
            "--command-id", Guid.NewGuid().ToString("D")
        ]);

        Assert.Equal(HostExecutionMode.ProvisionInitialAdmin, selection.Mode);
        Assert.False(selection.HasInvalidProvisioningInput);
        Assert.NotNull(selection.InitialProvisioningInput);
    }

    [Fact]
    public async Task Missing_secret_input_fails_before_web_startup()
    {
        var selection = HostCommandLine.Parse(
        [
            HostCommandLine.ProvisionInitialAdminCommand,
            "--operational-name", "Initial",
            "--login-identifier", "initial",
            "--command-id", Guid.NewGuid().ToString("D")
        ]);
        using var output = new StringWriter();

        var exitCode = await HostInitialProvisioningCommand.ExecuteAsync(
            selection,
            new StringReader(string.Empty),
            output,
            isStandardInputRedirected: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal("invalid_input", output.ToString().Trim());
    }

    [Fact]
    public async Task Missing_recovery_factor_input_fails_before_web_startup()
    {
        using var output = new StringWriter();

        var exitCode = await HostInitialProvisioningCommand.ExecuteAsync(
            ValidSelection(),
            new StringReader("credential-secret\n"),
            output,
            isStandardInputRedirected: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal("invalid_input", output.ToString().Trim());
    }

    [Fact]
    public async Task Invalid_or_additional_recovery_input_fails_before_web_startup()
    {
        using var invalidOutput = new StringWriter();
        var invalidExitCode = await HostInitialProvisioningCommand.ExecuteAsync(
            ValidSelection(),
            new StringReader("credential-secret\ninvalid-factor\n"),
            invalidOutput,
            isStandardInputRedirected: true,
            TestContext.Current.CancellationToken);

        using var additionalOutput = new StringWriter();
        var additionalExitCode = await HostInitialProvisioningCommand.ExecuteAsync(
            ValidSelection(),
            new StringReader($"credential-secret\n{Factor()}\nunexpected\n"),
            additionalOutput,
            isStandardInputRedirected: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, invalidExitCode);
        Assert.Equal("invalid_input", invalidOutput.ToString().Trim());
        Assert.Equal(2, additionalExitCode);
        Assert.Equal("invalid_input", additionalOutput.ToString().Trim());
    }

    [Fact]
    public async Task Interactive_standard_input_is_rejected()
    {
        using var output = new StringWriter();

        var exitCode = await HostInitialProvisioningCommand.ExecuteAsync(
            ValidSelection(),
            new StringReader($"credential-secret\n{Factor()}\n"),
            output,
            isStandardInputRedirected: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal("invalid_input", output.ToString().Trim());
    }

    [Fact]
    public void Normal_host_invocation_selects_web_mode()
    {
        var selection = HostCommandLine.Parse([]);

        Assert.Equal(HostExecutionMode.Web, selection.Mode);
        Assert.Null(selection.InitialProvisioningInput);
    }

    [Fact]
    public void Extraordinary_recovery_subcommand_selects_technical_mode()
    {
        var selection = HostCommandLine.Parse(
        [
            HostCommandLine.RecoverGeneralConfigurationCommand,
            "--command-id", Guid.NewGuid().ToString("D"),
            "--target-identity-id", Guid.NewGuid().ToString("D"),
            "--login-identifier", "recover.login"
        ]);

        Assert.Equal(HostExecutionMode.RecoverGeneralConfiguration, selection.Mode);
        Assert.False(selection.HasInvalidExtraordinaryRecoveryInput);
        Assert.NotNull(selection.ExtraordinaryRecoveryInput);
    }

    [Theory]
    [InlineData("not-a-guid", "00000000-0000-0000-0000-000000000001")]
    [InlineData("00000000-0000-0000-0000-000000000000", "00000000-0000-0000-0000-000000000001")]
    [InlineData("00000000-0000-4000-8000-000000000001", "not-a-guid")]
    [InlineData("00000000-0000-4000-8000-000000000001", "00000000-0000-0000-0000-000000000000")]
    public void Extraordinary_recovery_rejects_invalid_command_or_target_identifier(
        string commandId,
        string targetIdentityId)
    {
        var selection = HostCommandLine.Parse(
        [
            HostCommandLine.RecoverGeneralConfigurationCommand,
            "--command-id", commandId,
            "--target-identity-id", targetIdentityId
        ]);

        Assert.Equal(HostExecutionMode.RecoverGeneralConfiguration, selection.Mode);
        Assert.True(selection.HasInvalidExtraordinaryRecoveryInput);
    }

    [Fact]
    public async Task Extraordinary_recovery_rejects_interactive_missing_extra_and_invalid_factor_input()
    {
        using var interactiveOutput = new StringWriter();
        var interactive = await HostExtraordinaryGeneralConfigurationRecoveryCommand.ExecuteAsync(
            ValidExtraordinaryRecoverySelection(),
            new StringReader($"{Factor()}\nnew secret\n"),
            interactiveOutput,
            isStandardInputRedirected: false,
            TestContext.Current.CancellationToken);

        using var missingOutput = new StringWriter();
        var missing = await HostExtraordinaryGeneralConfigurationRecoveryCommand.ExecuteAsync(
            ValidExtraordinaryRecoverySelection(),
            new StringReader($"{Factor()}\n"),
            missingOutput,
            isStandardInputRedirected: true,
            TestContext.Current.CancellationToken);

        using var extraOutput = new StringWriter();
        var extra = await HostExtraordinaryGeneralConfigurationRecoveryCommand.ExecuteAsync(
            ValidExtraordinaryRecoverySelection(),
            new StringReader($"{Factor()}\nnew secret\nextra\n"),
            extraOutput,
            isStandardInputRedirected: true,
            TestContext.Current.CancellationToken);

        using var invalidFactorOutput = new StringWriter();
        var invalidFactor = await HostExtraordinaryGeneralConfigurationRecoveryCommand.ExecuteAsync(
            ValidExtraordinaryRecoverySelection(),
            new StringReader("invalid\nnew secret\n"),
            invalidFactorOutput,
            isStandardInputRedirected: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, interactive);
        Assert.Equal(2, missing);
        Assert.Equal(2, extra);
        Assert.Equal(2, invalidFactor);
        Assert.Equal("invalid_input", interactiveOutput.ToString().Trim());
        Assert.Equal("invalid_input", missingOutput.ToString().Trim());
        Assert.Equal("invalid_input", extraOutput.ToString().Trim());
        Assert.Equal("invalid_input", invalidFactorOutput.ToString().Trim());
    }

    [Fact]
    public async Task Real_host_process_provisions_from_redirected_stdin_without_web_server()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = new IdentitiesAndCapabilitiesFixture();
        await fixture.InitializeAsync();
        await fixture.ResetAsync(token);
        var commandId = Guid.NewGuid();

        var first = await RunHostAsync(
            fixture.ConnectionString,
            commandId,
            "Initial Process Administrator",
            "process-admin",
            "process-secret",
            Factor(),
            token);

        Assert.Equal(0, first.ExitCode);
        Assert.Contains("success", first.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(
            LoginOutcome.Succeeded,
            (await fixture.LoginAsync("process-admin", "process-secret", token)).Outcome);
        var recoveryState = await fixture.ReadInstallationRecoveryStateAsync(token);
        Assert.NotNull(recoveryState);
        Assert.Equal(1, recoveryState.Generation);

        var second = await RunHostAsync(
            fixture.ConnectionString,
            Guid.NewGuid(),
            "Second Process Administrator",
            "second-process-admin",
            "second-process-secret",
            Factor(),
            token);

        Assert.Equal(3, second.ExitCode);
        Assert.Contains(
            "already_initialized",
            second.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_host_process_recovers_an_inactive_initial_administrator_and_restores_ordinary_access()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = new IdentitiesAndCapabilitiesFixture();
        await fixture.InitializeAsync();
        await fixture.ResetAsync(token);
        fixture.Clock.SetUtcNow(TimeProvider.System.GetUtcNow());
        var recoveryFactor = Factor();
        const string loginIdentifier = "recovered-process-admin";
        const string initialSecret = "initial-process-secret";
        const string recoveredSecret = "recovered-process-secret";

        var provisioning = await RunHostAsync(
            fixture.ConnectionString,
            Guid.NewGuid(),
            "Recoverable Process Administrator",
            loginIdentifier,
            initialSecret,
            recoveryFactor,
            token);

        Assert.Equal(0, provisioning.ExitCode);
        Assert.Contains("success", provisioning.StandardOutput, StringComparison.Ordinal);
        AssertOperatorSafeOutput(provisioning, recoveryFactor, initialSecret);
        Assert.NotNull(await fixture.ReadInstallationRecoveryStateAsync(token));
        var provisioningFact = Assert.IsType<InstallationProvisioningFactSnapshot>(
            await fixture.ReadInstallationProvisioningFactAsync(token));
        var targetIdentityId = Assert.IsType<Guid>(provisioningFact.InitialIdentityId);

        using var oldSessionClient = fixture.CreateClient();
        using (var initialLogin = await LoginHttpAsync(
            oldSessionClient,
            loginIdentifier,
            initialSecret,
            token))
        {
            Assert.Equal(HttpStatusCode.OK, initialLogin.StatusCode);
        }
        using (var initialCurrent = await oldSessionClient.GetAsync(
            "/api/identity-sessions/current",
            token))
        {
            var current = await initialCurrent.Content.ReadFromJsonAsync<CurrentIdentityResponse>(
                cancellationToken: token);
            Assert.Equal(HttpStatusCode.OK, initialCurrent.StatusCode);
            Assert.NotNull(current);
            Assert.Equal(targetIdentityId, current.IdentityId);
            Assert.Contains("GeneralConfiguration", current.Responsibilities);
        }

        // This is the test-only external-loss transition; ordinary safeguards prevent it.
        await fixture.SetIdentityActiveAsync(targetIdentityId, false, token);
        using (var unavailableClient = fixture.CreateClient())
        using (var unavailableLogin = await LoginHttpAsync(
            unavailableClient,
            loginIdentifier,
            initialSecret,
            token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unavailableLogin.StatusCode);
        }

        var recoveryCommandId = Guid.NewGuid();
        var recovery = await RunRecoveryHostAsync(
            fixture.ConnectionString,
            recoveryCommandId,
            targetIdentityId,
            recoveryFactor,
            recoveredSecret,
            token);

        Assert.Equal(0, recovery.ExitCode);
        Assert.Contains("success", recovery.StandardOutput, StringComparison.Ordinal);
        var recoveredVerifier = await fixture.ReadCredentialVerifierAsync(targetIdentityId, token);
        AssertOperatorSafeOutput(
            recovery,
            recoveryFactor,
            initialSecret,
            recoveredSecret,
            recoveredVerifier);

        using (var oldSessionCurrent = await oldSessionClient.GetAsync(
            "/api/identity-sessions/current",
            token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldSessionCurrent.StatusCode);
        }
        using (var oldCredentialClient = fixture.CreateClient())
        using (var oldCredentialLogin = await LoginHttpAsync(
            oldCredentialClient,
            loginIdentifier,
            initialSecret,
            token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldCredentialLogin.StatusCode);
        }

        using var recoveredClient = fixture.CreateClient();
        using (var recoveredLogin = await LoginHttpAsync(
            recoveredClient,
            loginIdentifier,
            recoveredSecret,
            token))
        {
            Assert.Equal(HttpStatusCode.OK, recoveredLogin.StatusCode);
        }
        using (var currentResponse = await recoveredClient.GetAsync(
            "/api/identity-sessions/current",
            token))
        {
            Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
            var current = await currentResponse.Content.ReadFromJsonAsync<CurrentIdentityResponse>(
                cancellationToken: token);
            Assert.NotNull(current);
            Assert.Equal(targetIdentityId, current.IdentityId);
            Assert.Contains("GeneralConfiguration", current.Responsibilities);
        }
        using (var identities = await recoveredClient.GetAsync("/api/identities", token))
        {
            Assert.Equal(HttpStatusCode.OK, identities.StatusCode);
        }

        var replay = await RunRecoveryHostAsync(
            fixture.ConnectionString,
            recoveryCommandId,
            targetIdentityId,
            recoveryFactor,
            recoveredSecret,
            token);

        Assert.Equal(0, replay.ExitCode);
        Assert.Contains("replayed_success", replay.StandardOutput, StringComparison.Ordinal);
        AssertOperatorSafeOutput(
            replay,
            recoveryFactor,
            initialSecret,
            recoveredSecret,
            recoveredVerifier);
        Assert.Equal(1, await fixture.CountExtraordinaryRecoveryCommandsAsync(token));
        using var replayedLoginClient = fixture.CreateClient();
        using var replayedLogin = await LoginHttpAsync(
            replayedLoginClient,
            loginIdentifier,
            recoveredSecret,
            token);
        Assert.Equal(HttpStatusCode.OK, replayedLogin.StatusCode);
    }

    private static async Task<HostProcessResult> RunHostAsync(
        string connectionString,
        Guid commandId,
        string operationalName,
        string loginIdentifier,
        string secret,
        string recoveryFactor,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add(HostCommandLine.ProvisionInitialAdminCommand);
        startInfo.ArgumentList.Add("--operational-name");
        startInfo.ArgumentList.Add(operationalName);
        startInfo.ArgumentList.Add("--login-identifier");
        startInfo.ArgumentList.Add(loginIdentifier);
        startInfo.ArgumentList.Add("--command-id");
        startInfo.ArgumentList.Add(commandId.ToString("D"));
        startInfo.Environment["ConnectionStrings__IdentitiesAndCapabilities"] =
            connectionString;

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("The Host process could not start.");
        await process.StandardInput.WriteLineAsync(secret);
        await process.StandardInput.WriteLineAsync(recoveryFactor);
        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new HostProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static async Task<HostProcessResult> RunRecoveryHostAsync(
        string connectionString,
        Guid commandId,
        Guid targetIdentityId,
        string recoveryFactor,
        string newCredentialSecret,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add(HostCommandLine.RecoverGeneralConfigurationCommand);
        startInfo.ArgumentList.Add("--command-id");
        startInfo.ArgumentList.Add(commandId.ToString("D"));
        startInfo.ArgumentList.Add("--target-identity-id");
        startInfo.ArgumentList.Add(targetIdentityId.ToString("D"));
        startInfo.Environment["ConnectionStrings__IdentitiesAndCapabilities"] =
            connectionString;

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("The Host process could not start.");
        await process.StandardInput.WriteLineAsync(recoveryFactor);
        await process.StandardInput.WriteLineAsync(newCredentialSecret);
        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new HostProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static async Task<HttpResponseMessage> LoginHttpAsync(
        HttpClient client,
        string loginIdentifier,
        string secret,
        CancellationToken cancellationToken)
    {
        using var antiforgeryResponse = await client.GetAsync(
            "/api/security/antiforgery",
            cancellationToken);
        antiforgeryResponse.EnsureSuccessStatusCode();
        var antiforgery = await antiforgeryResponse.Content.ReadFromJsonAsync<AntiforgeryTokenResponse>(
            cancellationToken: cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity-sessions")
        {
            Content = JsonContent.Create(new { loginIdentifier, secret })
        };
        request.Headers.Add(
            "X-NexoBar-CSRF",
            Assert.IsType<AntiforgeryTokenResponse>(antiforgery).RequestToken);
        return await client.SendAsync(request, cancellationToken);
    }

    private static void AssertOperatorSafeOutput(
        HostProcessResult result,
        params string[] protectedValues)
    {
        foreach (var protectedValue in protectedValues)
        {
            Assert.DoesNotContain(protectedValue, result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(protectedValue, result.StandardError, StringComparison.Ordinal);
        }
    }

    private sealed record HostProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private static HostCommandSelection ValidSelection() => HostCommandLine.Parse(
    [
        HostCommandLine.ProvisionInitialAdminCommand,
        "--operational-name", "Initial",
        "--login-identifier", "initial",
        "--command-id", Guid.NewGuid().ToString("D")
    ]);

    private static HostCommandSelection ValidExtraordinaryRecoverySelection() =>
        HostCommandLine.Parse(
        [
            HostCommandLine.RecoverGeneralConfigurationCommand,
            "--command-id", Guid.NewGuid().ToString("D"),
            "--target-identity-id", Guid.NewGuid().ToString("D")
        ]);

    private static string Factor() =>
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
