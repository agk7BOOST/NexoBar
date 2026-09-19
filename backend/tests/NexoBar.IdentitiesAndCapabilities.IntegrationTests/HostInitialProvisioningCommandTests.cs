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
}
