using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Host;

internal static class HostExtraordinaryGeneralConfigurationRecoveryCommand
{
    internal static async Task<int> ExecuteAsync(
        HostCommandSelection selection,
        TextReader standardInput,
        TextWriter standardOutput,
        bool isStandardInputRedirected,
        CancellationToken cancellationToken)
    {
        if (selection.Mode != HostExecutionMode.RecoverGeneralConfiguration ||
            selection.HasInvalidExtraordinaryRecoveryInput ||
            selection.ExtraordinaryRecoveryInput is null ||
            !isStandardInputRedirected)
        {
            await standardOutput.WriteLineAsync("invalid_input");
            return 2;
        }

        var recoveryFactor = await standardInput.ReadLineAsync(cancellationToken);
        var newCredentialSecret = await standardInput.ReadLineAsync(cancellationToken);
        var unexpectedInput = await standardInput.ReadLineAsync(cancellationToken);
        if (!RecoveryFactorFormat.IsCanonical(recoveryFactor) ||
            string.IsNullOrWhiteSpace(newCredentialSecret) || unexpectedInput is not null)
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
            var input = selection.ExtraordinaryRecoveryInput;
            var mode = input.LoginIdentifier is null
                ? ExtraordinaryRecoveryLoginIntentMode.PreserveExisting
                : ExtraordinaryRecoveryLoginIntentMode.ExplicitIdentifier;
            var result = await scope.ServiceProvider
                .GetRequiredService<IExtraordinaryGeneralConfigurationRecoveryService>()
                .RecoverAsync(
                    new ExtraordinaryGeneralConfigurationRecoveryRequest(
                        input.CommandId,
                        input.TargetIdentityId,
                        mode,
                        input.LoginIdentifier,
                        recoveryFactor,
                        newCredentialSecret),
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
        ExtraordinaryGeneralConfigurationRecoveryResult result) =>
        result.Outcome is ExtraordinaryGeneralConfigurationRecoveryOutcome.Succeeded or
            ExtraordinaryGeneralConfigurationRecoveryOutcome.ReplayedSuccess
            ? standardOutput.WriteLineAsync(
                $"{OutcomeName(result.Outcome)} command_id={result.CommandId:D} target_identity_id={result.TargetIdentityId:D} recovery_factor_generation={result.RecoveryFactorGeneration} completed_at={result.CompletedAt:O}")
            : standardOutput.WriteLineAsync(OutcomeName(result.Outcome));

    private static int ExitCode(ExtraordinaryGeneralConfigurationRecoveryOutcome outcome) =>
        outcome switch
        {
            ExtraordinaryGeneralConfigurationRecoveryOutcome.Succeeded or
                ExtraordinaryGeneralConfigurationRecoveryOutcome.ReplayedSuccess => 0,
            ExtraordinaryGeneralConfigurationRecoveryOutcome.InfrastructureFailure => 1,
            ExtraordinaryGeneralConfigurationRecoveryOutcome.InvalidInput => 2,
            ExtraordinaryGeneralConfigurationRecoveryOutcome.InvalidRecoveryFactor => 3,
            ExtraordinaryGeneralConfigurationRecoveryOutcome.TargetNotFound => 4,
            ExtraordinaryGeneralConfigurationRecoveryOutcome.DuplicateLogin => 5,
            ExtraordinaryGeneralConfigurationRecoveryOutcome.IntentConflict => 6,
            ExtraordinaryGeneralConfigurationRecoveryOutcome.RecoveryNotConfigured => 7,
            _ => throw new InvalidOperationException("Unknown extraordinary recovery outcome.")
        };

    private static string OutcomeName(
        ExtraordinaryGeneralConfigurationRecoveryOutcome outcome) => outcome switch
        {
            ExtraordinaryGeneralConfigurationRecoveryOutcome.Succeeded => "success",
            ExtraordinaryGeneralConfigurationRecoveryOutcome.ReplayedSuccess => "replayed_success",
            ExtraordinaryGeneralConfigurationRecoveryOutcome.InfrastructureFailure => "infrastructure_failure",
            ExtraordinaryGeneralConfigurationRecoveryOutcome.InvalidInput => "invalid_input",
            ExtraordinaryGeneralConfigurationRecoveryOutcome.InvalidRecoveryFactor => "invalid_recovery_factor",
            ExtraordinaryGeneralConfigurationRecoveryOutcome.TargetNotFound => "target_not_found",
            ExtraordinaryGeneralConfigurationRecoveryOutcome.DuplicateLogin => "duplicate_login",
            ExtraordinaryGeneralConfigurationRecoveryOutcome.IntentConflict => "intent_conflict",
            ExtraordinaryGeneralConfigurationRecoveryOutcome.RecoveryNotConfigured => "recovery_not_configured",
            _ => throw new InvalidOperationException("Unknown extraordinary recovery outcome.")
        };
}
