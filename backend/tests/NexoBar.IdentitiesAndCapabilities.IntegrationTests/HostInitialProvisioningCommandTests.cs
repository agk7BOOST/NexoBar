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
    public void Normal_host_invocation_selects_web_mode()
    {
        var selection = HostCommandLine.Parse([]);

        Assert.Equal(HostExecutionMode.Web, selection.Mode);
        Assert.Null(selection.InitialProvisioningInput);
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
            token);

        Assert.Equal(0, first.ExitCode);
        Assert.Contains("success", first.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(
            LoginOutcome.Succeeded,
            (await fixture.LoginAsync("process-admin", "process-secret", token)).Outcome);

        var second = await RunHostAsync(
            fixture.ConnectionString,
            Guid.NewGuid(),
            "Second Process Administrator",
            "second-process-admin",
            "second-process-secret",
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
}
