using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Host;

internal static class HostCommandLine
{
    internal const string ProvisionInitialAdminCommand = "provision-initial-admin";

    internal static HostCommandSelection Parse(string[] args)
    {
        if (args.Length == 0 || !string.Equals(
                args[0],
                ProvisionInitialAdminCommand,
                StringComparison.Ordinal))
        {
            return HostCommandSelection.Web();
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(args[index], args[index + 1]))
            {
                return HostCommandSelection.InvalidProvisioningInput();
            }
        }

        if (values.Keys.Any(option => option is not "--operational-name" and not "--login-identifier" and not "--command-id") ||
            !values.TryGetValue("--operational-name", out var operationalName) ||
            !values.TryGetValue("--login-identifier", out var loginIdentifier) ||
            !values.TryGetValue("--command-id", out var commandIdText) ||
            !Guid.TryParse(commandIdText, out var commandId) ||
            !IsUuidVersion4(commandId))
        {
            return HostCommandSelection.InvalidProvisioningInput();
        }

        return HostCommandSelection.ProvisionInitialAdmin(
            new HostInitialProvisioningInput(commandId, operationalName, loginIdentifier));
    }

    private static bool IsUuidVersion4(Guid value) =>
        value != Guid.Empty && (value.ToByteArray(bigEndian: true)[6] >> 4) == 4;
}

internal sealed record HostCommandSelection(
    HostExecutionMode Mode,
    HostInitialProvisioningInput? InitialProvisioningInput,
    bool HasInvalidProvisioningInput)
{
    internal static HostCommandSelection Web() =>
        new(HostExecutionMode.Web, null, false);

    internal static HostCommandSelection ProvisionInitialAdmin(
        HostInitialProvisioningInput input) =>
        new(HostExecutionMode.ProvisionInitialAdmin, input, false);

    internal static HostCommandSelection InvalidProvisioningInput() =>
        new(HostExecutionMode.ProvisionInitialAdmin, null, true);
}

internal enum HostExecutionMode
{
    Web,
    ProvisionInitialAdmin
}

internal sealed record HostInitialProvisioningInput(
    Guid CommandId,
    string OperationalName,
    string LoginIdentifier);

internal static class HostInitialProvisioningCommand
{
    internal static async Task<int> ExecuteAsync(
        HostCommandSelection selection,
        TextReader standardInput,
        TextWriter standardOutput,
        bool isStandardInputRedirected,
        CancellationToken cancellationToken)
    {
        if (selection.Mode != HostExecutionMode.ProvisionInitialAdmin ||
            selection.HasInvalidProvisioningInput ||
            selection.InitialProvisioningInput is null ||
            !isStandardInputRedirected)
        {
            await standardOutput.WriteLineAsync("invalid_input");
            return 2;
        }

        var secret = await standardInput.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(secret))
        {
            await standardOutput.WriteLineAsync("invalid_input");
            return 2;
        }

        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Services.AddIdentitiesAndCapabilities(builder.Configuration);
            await using var serviceProvider = builder.Services.BuildServiceProvider();
            await using var scope = serviceProvider.CreateAsyncScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IInitialProvisioningService>()
                .ProvisionAsync(
                    new InitialProvisioningRequest(
                        selection.InitialProvisioningInput.CommandId,
                        selection.InitialProvisioningInput.OperationalName,
                        selection.InitialProvisioningInput.LoginIdentifier,
                        secret),
                    cancellationToken);
            await WriteResultAsync(standardOutput, result);
            return ExitCode(result.Outcome);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await standardOutput.WriteLineAsync("infrastructure_failure");
            return 1;
        }
    }

    private static Task WriteResultAsync(
        TextWriter standardOutput,
        InitialProvisioningResult result) =>
        result.Outcome is InitialProvisioningOutcome.Succeeded or
            InitialProvisioningOutcome.ReplayedSuccess
            ? standardOutput.WriteLineAsync(
                $"{OutcomeName(result.Outcome)} identity_id={result.IdentityId:D} command_id={result.CommandId:D}")
            : standardOutput.WriteLineAsync(OutcomeName(result.Outcome));

    private static int ExitCode(InitialProvisioningOutcome outcome) => outcome switch
    {
        InitialProvisioningOutcome.Succeeded or
            InitialProvisioningOutcome.ReplayedSuccess => 0,
        InitialProvisioningOutcome.InvalidInput => 2,
        InitialProvisioningOutcome.AlreadyInitialized => 3,
        InitialProvisioningOutcome.IntentConflict => 4,
        InitialProvisioningOutcome.DuplicateLogin => 5,
        InitialProvisioningOutcome.InfrastructureFailure => 1,
        _ => throw new InvalidOperationException("Unknown initial provisioning outcome.")
    };

    private static string OutcomeName(InitialProvisioningOutcome outcome) => outcome switch
    {
        InitialProvisioningOutcome.Succeeded => "success",
        InitialProvisioningOutcome.ReplayedSuccess => "replayed_success",
        InitialProvisioningOutcome.AlreadyInitialized => "already_initialized",
        InitialProvisioningOutcome.IntentConflict => "intent_conflict",
        InitialProvisioningOutcome.InvalidInput => "invalid_input",
        InitialProvisioningOutcome.DuplicateLogin => "duplicate_login",
        InitialProvisioningOutcome.InfrastructureFailure => "infrastructure_failure",
        _ => throw new InvalidOperationException("Unknown initial provisioning outcome.")
    };
}
