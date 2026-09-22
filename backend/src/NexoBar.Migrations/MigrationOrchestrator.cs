using Microsoft.Extensions.Logging;

namespace NexoBar.Migrations;

public static class MigrationOrchestrator
{
    public static async Task<int> RunAsync(
        IReadOnlyList<MigrationStep> steps,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken,
        Action<string>? moduleCompleted = null)
    {
        foreach (var step in steps)
        {
            logger.LogInformation("Migrating {Module}.", step.Module);
            try
            {
                await step.ApplyAsync(services, cancellationToken);
                logger.LogInformation("Migration completed for {Module}.", step.Module);
                moduleCompleted?.Invoke(step.Module);
            }
            catch (Exception exception)
            {
                // Provider exception text can include connection details; keep output secret-safe.
                logger.LogError(
                    "Migration failed for {Module} ({ExceptionType}); later modules were not attempted.",
                    step.Module,
                    exception.GetType().Name);
                return 1;
            }
        }

        logger.LogInformation("All NexoBar persistence migrations completed.");
        return 0;
    }
}
