using System.Diagnostics;
using NexoBar.Host;

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
