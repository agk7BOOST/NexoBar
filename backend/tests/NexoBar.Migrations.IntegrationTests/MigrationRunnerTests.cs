using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexoBar.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace NexoBar.Migrations.IntegrationTests;

public sealed class MigrationRunnerTests
{
    [Fact]
    public async Task Production_plan_migrates_empty_database_in_order_and_second_run_is_no_op()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await CreatePostgresAsync();
        var connectionString = postgres.GetConnectionString();
        await using var services = MigrationServices.Build(Configuration(connectionString));
        await using (var emptyDatabase = new NpgsqlConnection(connectionString))
        {
            await emptyDatabase.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM information_schema.schemata WHERE schema_name IN " +
                "('catalog', 'identities_and_capabilities', 'inventory', 'operational_configuration', 'order_operations')",
                emptyDatabase);
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(cancellationToken))!);
        }
        var firstRun = await RunProductionEntrypointAsync(connectionString, cancellationToken);
        Assert.Equal(0, firstRun.ExitCode);
        Assert.True(
            firstRun.Output.IndexOf("Migrating OperationalConfiguration", StringComparison.Ordinal) <
            firstRun.Output.IndexOf("Migrating OrderOperations", StringComparison.Ordinal));
        Assert.True(
            firstRun.Output.IndexOf("Migration completed for OperationalConfiguration", StringComparison.Ordinal) <
            firstRun.Output.IndexOf("Migrating OrderOperations", StringComparison.Ordinal));

        var migrationsAfterFirstRun = await ReadAppliedMigrationsAsync(services, cancellationToken);
        Assert.All(MigrationPlan.Steps, step => Assert.False(step.HasPendingModelChanges(services)));
        Assert.All(migrationsAfterFirstRun.Values, migrations => Assert.NotEmpty(migrations));

        var schemaNames = new[]
        {
            "operational_configuration",
            "identities_and_capabilities",
            "catalog",
            "inventory",
            "order_operations"
        };
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            foreach (var schema in schemaNames)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT to_regclass(format('%I.__ef_migrations_history', @schema)) IS NOT NULL",
                    connection);
                command.Parameters.AddWithValue("schema", schema);
                Assert.True((bool)(await command.ExecuteScalarAsync(cancellationToken))!);
            }
        }

        var secondRun = await RunProductionEntrypointAsync(connectionString, cancellationToken);
        Assert.Equal(0, secondRun.ExitCode);
        var migrationsAfterSecondRun = await ReadAppliedMigrationsAsync(services, cancellationToken);
        foreach (var module in migrationsAfterFirstRun.Keys)
        {
            Assert.Equal(migrationsAfterFirstRun[module], migrationsAfterSecondRun[module]);
        }
    }

    [Fact]
    public async Task Orchestrator_stops_after_first_failed_module()
    {
        var attempted = new List<string>();
        var steps = new[]
        {
            Step("OperationalConfiguration", attempted, shouldFail: true),
            Step("OrderOperations", attempted, shouldFail: false)
        };

        var result = await MigrationOrchestrator.RunAsync(
            steps,
            new Microsoft.Extensions.DependencyInjection.ServiceCollection()
                .BuildServiceProvider(),
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        Assert.Equal(["OperationalConfiguration"], attempted);
    }

    [Fact]
    public async Task Ordinary_host_startup_does_not_create_database_schemas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await CreatePostgresAsync();
        var connectionString = postgres.GetConnectionString();
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(
                    ConnectionStrings(connectionString))));

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", cancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name IN " +
            "('catalog', 'identities_and_capabilities', 'inventory', 'operational_configuration', 'order_operations')",
            connection);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    private static async Task<PostgreSqlContainer> CreatePostgresAsync()
    {
        var postgres = new PostgreSqlBuilder("postgres:17.6")
            .WithDatabase("nexobar_migrations_tests")
            .WithUsername("nexobar_tests")
            .WithPassword("nexobar_tests_password")
            .Build();
        await postgres.StartAsync();
        return postgres;
    }

    private static IConfiguration Configuration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(ConnectionStrings(connectionString))
            .Build();

    private static Dictionary<string, string?> ConnectionStrings(string connectionString) =>
        new()
        {
            ["ConnectionStrings:OperationalConfiguration"] = connectionString,
            ["ConnectionStrings:IdentitiesAndCapabilities"] = connectionString,
            ["ConnectionStrings:Catalog"] = connectionString,
            ["ConnectionStrings:Inventory"] = connectionString,
            ["ConnectionStrings:OrderOperations"] = connectionString
        };

    private static async Task<Dictionary<string, string[]>> ReadAppliedMigrationsAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string[]>();
        foreach (var step in MigrationPlan.Steps)
        {
            result.Add(
                step.Module,
                (await step.ReadAppliedMigrationsAsync(services, cancellationToken)).ToArray());
        }

        return result;
    }

    private static async Task<(int ExitCode, string Output)> RunProductionEntrypointAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repositoryRoot is not null &&
               !File.Exists(Path.Combine(repositoryRoot.FullName, "backend", "NexoBar.slnx")))
        {
            repositoryRoot = repositoryRoot.Parent;
        }

        Assert.NotNull(repositoryRoot);
        var projectPath = Path.Combine(
            repositoryRoot!.FullName,
            "backend",
            "src",
            "NexoBar.Migrations",
            "NexoBar.Migrations.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        foreach (var module in new[]
                 {
                     "OperationalConfiguration",
                     "IdentitiesAndCapabilities",
                     "Catalog",
                     "Inventory",
                     "OrderOperations"
                 })
        {
            startInfo.Environment[$"ConnectionStrings__{module}"] = connectionString;
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process!.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, (await standardOutput) + (await standardError));
    }

    private static MigrationStep Step(
        string module,
        ICollection<string> attempted,
        bool shouldFail) => new(
            module,
            (_, _) =>
            {
                attempted.Add(module);
                return shouldFail
                    ? Task.FromException(new InvalidOperationException("test failure"))
                    : Task.CompletedTask;
            },
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
            _ => false);
}
