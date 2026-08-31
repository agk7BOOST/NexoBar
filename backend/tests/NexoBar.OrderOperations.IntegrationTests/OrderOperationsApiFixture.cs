using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OperationalConfiguration;
using NexoBar.OrderOperations;
using Testcontainers.PostgreSql;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed class OrderOperationsApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.6")
        .WithDatabase("nexobar_order_operations_tests")
        .WithUsername("nexobar_tests")
        .WithPassword("nexobar_tests_password")
        .Build();

    private WebApplicationFactory<Program>? application;

    internal HttpClient Client { get; private set; } = null!;
    internal string ConnectionString => postgres.GetConnectionString();
    internal IServiceProvider Services => application!.Services;

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        StartApplication();

        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OperationalConfigurationDbContext>()
            .Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<CatalogDbContext>()
            .Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>()
            .Database.MigrateAsync();
    }

    internal async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                order_operations.delivery_states,
                order_operations.preparation_commands,
                order_operations.preparation_history,
                order_operations.subsequent_confirmation_command_contents,
                order_operations.subsequent_confirmation_commands,
                order_operations.first_confirmation_command_contents,
                order_operations.first_confirmation_commands,
                order_operations.preparation_work,
                order_operations.confirmation_history,
                order_operations.incorporation_contents,
                order_operations.incorporations,
                order_operations.orders,
                catalog.product_preparation_configuration_change_commands,
                catalog.product_price_change_commands,
                catalog.product_creation_commands,
                catalog.products,
                identities_and_capabilities.administrative_commands,
                identities_and_capabilities.sessions,
                identities_and_capabilities.local_credentials,
                identities_and_capabilities.preparation_enablements,
                identities_and_capabilities.responsibility_assignments,
                identities_and_capabilities.identities,
                operational_configuration.preparation_responsibility_creation_commands,
                operational_configuration.preparation_responsibilities
            """,
            cancellationToken);
    }

    internal async Task<PreparationActor> CreatePreparationActorAsync(
        bool hasPreparation,
        Guid? enabledResponsibilityId,
        CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var loginIdentifier = $"preparer-{suffix}";
        const string secret = "preparation-test-secret";
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
        var identity = new Identity($"Preparador {suffix}", true);
        dbContext.Identities.Add(identity);
        if (hasPreparation)
        {
            dbContext.ResponsibilityAssignments.Add(new ResponsibilityAssignment(
                identity.Id,
                FunctionalResponsibility.Preparation));
        }

        if (enabledResponsibilityId is { } responsibilityId)
        {
            dbContext.PreparationEnablements.Add(new PreparationEnablement(
                identity.Id,
                responsibilityId));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<LocalCredentialProvisioner>()
            .ProvisionAsync(
                identity.Id,
                loginIdentifier,
                secret,
                cancellationToken);
        return new PreparationActor(identity.Id, loginIdentifier, secret);
    }

    internal async Task<HttpClient> LoginAsync(
        PreparationActor actor,
        CancellationToken cancellationToken,
        WebApplicationFactory<Program>? targetApplication = null)
    {
        var client = (targetApplication ?? application!).CreateClient();
        using var antiforgery = await client.GetAsync(
            "/api/security/antiforgery",
            cancellationToken);
        antiforgery.EnsureSuccessStatusCode();
        using var document = await System.Text.Json.JsonDocument.ParseAsync(
            await antiforgery.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var requestToken = document.RootElement.GetProperty("requestToken").GetString();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/identity-sessions")
        {
            Content = JsonContent.Create(new
            {
                loginIdentifier = actor.LoginIdentifier,
                secret = actor.Secret
            })
        };
        request.Headers.Add("X-NexoBar-CSRF", requestToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return client;
    }

    internal async Task SetIdentityActiveAsync(
        Guid identityId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .Identities.Where(identity => identity.Id == identityId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(identity => identity.IsActive, isActive),
                cancellationToken);
    }

    internal async Task RevokePreparationAssignmentAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .ResponsibilityAssignments.Where(assignment =>
                assignment.IdentityId == identityId &&
                assignment.ResponsibilityCode == FunctionalResponsibility.Preparation)
            .ExecuteDeleteAsync(cancellationToken);
    }

    internal async Task RevokePreparationEnablementAsync(
        Guid identityId,
        Guid preparationResponsibilityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .PreparationEnablements.Where(enablement =>
                enablement.IdentityId == identityId &&
                enablement.PreparationResponsibilityId == preparationResponsibilityId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    internal async Task RevokeSessionsAsync(
        Guid identityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var now = DateTimeOffset.UtcNow;
        await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
            .Sessions.Where(session =>
                session.IdentityId == identityId && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAt, now),
                cancellationToken);
    }

    internal async Task RenameProductAsync(
        Guid productId,
        string operationalName,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CatalogDbContext>()
            .Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE catalog.products SET operational_name = {operationalName} WHERE id = {productId}",
                cancellationToken);
    }

    internal async Task ReplaceContentProductReferenceAsync(
        Guid incorporationId,
        int contentOrdinal,
        Guid productId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>()
            .Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE order_operations.incorporation_contents
                SET product_id = {productId}
                WHERE incorporation_id = {incorporationId}
                  AND content_ordinal = {contentOrdinal}
                """,
                cancellationToken);
    }

    internal async Task<ProductResponse> CreateProductAsync(
        string name,
        string price,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/products")
        {
            Content = JsonContent.Create(new CreateProductRequest(name, price, false))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));

        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Assert.IsType<ProductResponse>(
            await response.Content.ReadFromJsonAsync<ProductResponse>(cancellationToken));
    }

    internal async Task<(ProductResponse Product, FirstConfirmationResponse Confirmation)>
        CreatePreparedWorkAsync(
            Guid preparationResponsibilityId,
            CancellationToken cancellationToken,
            int quantity = 1,
            string productName = "Preparado")
    {
        var product = await CreateProductAsync(productName, "7", cancellationToken);
        await SetProductPreparationAsync(
            product.Id,
            preparationResponsibilityId,
            cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                "Mesa concurrente",
                [new FirstConfirmationItemRequest(product.Id, quantity)]))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var confirmation = Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(
                cancellationToken));
        return (product, confirmation);
    }

    internal async Task SetProductStateAsync(
        Guid productId,
        bool isActive,
        bool isAvailable,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE catalog.products
            SET is_active = {isActive}, is_available = {isAvailable}
            WHERE id = {productId}
            """,
            cancellationToken);
    }

    internal async Task SetProductPreparationAsync(
        Guid productId,
        Guid? preparationResponsibilityId,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE catalog.products
            SET requires_preparation = {preparationResponsibilityId is not null},
                preparation_responsibility_id = {preparationResponsibilityId}
            WHERE id = {productId}
            """,
            cancellationToken);
    }

    internal async Task SetOrderContextAsync(
        Guid orderId,
        string context,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE order_operations.orders SET context = {context} WHERE id = {orderId}",
            cancellationToken);
    }

    internal async Task<IReadOnlyList<PreparationWorkSnapshot>> ReadPreparationWorkAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        return await (
            from work in dbContext.PreparationWork.AsNoTracking()
            join content in dbContext.IncorporationContents.AsNoTracking()
                on new { work.IncorporationId, work.ContentOrdinal }
                equals new { content.IncorporationId, content.ContentOrdinal }
            orderby work.IncorporationId, work.ContentOrdinal
            select new PreparationWorkSnapshot(
                work.Id,
                work.IncorporationId,
                work.ContentOrdinal,
                content.ProductId,
                content.Instruction,
                work.PreparationResponsibilityId,
                work.TotalQuantity,
                work.PendingQuantity,
                work.InPreparationQuantity,
                work.ReadyQuantity)).ToArrayAsync(cancellationToken);
    }

    internal async Task<int> CountPreparationWorkAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>()
            .PreparationWork.CountAsync(cancellationToken);
    }

    internal async Task<IReadOnlyList<DeliveryStateSnapshot>> ReadDeliveryStatesAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        return await (
            from state in dbContext.DeliveryStates.AsNoTracking()
            join content in dbContext.IncorporationContents.AsNoTracking()
                on new { state.IncorporationId, state.ContentOrdinal }
                equals new { content.IncorporationId, content.ContentOrdinal }
            orderby state.IncorporationId, state.ContentOrdinal
            select new DeliveryStateSnapshot(
                state.IncorporationId,
                state.ContentOrdinal,
                content.ProductId,
                content.Instruction,
                state.DeliveredQuantity)).ToArrayAsync(cancellationToken);
    }

    internal async Task<int> CountDeliveryStatesAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>()
            .DeliveryStates.CountAsync(cancellationToken);
    }

    internal async Task<IReadOnlyList<ConfirmedContentSnapshot>>
        ReadConfirmedContentsAsync(CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        return await (
            from content in dbContext.IncorporationContents.AsNoTracking()
            join incorporation in dbContext.Incorporations.AsNoTracking()
                on content.IncorporationId equals incorporation.Id
            orderby incorporation.Ordinal, content.ContentOrdinal
            select new ConfirmedContentSnapshot(
                incorporation.OrderId,
                incorporation.Id,
                incorporation.Ordinal,
                content.ContentOrdinal,
                content.ProductId,
                content.RequiresPreparationAtConfirmation))
            .ToArrayAsync(cancellationToken);
    }

    internal async Task<IReadOnlyList<PreparationHistory>> ReadPreparationHistoryAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>()
            .PreparationHistory.AsNoTracking()
            .OrderBy(history => history.OccurredAt)
            .ThenBy(history => history.Id)
            .ToArrayAsync(cancellationToken);
    }

    internal async Task<IReadOnlyList<PreparationCommand>> ReadPreparationCommandsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>()
            .PreparationCommands.AsNoTracking()
            .OrderBy(command => command.IdempotencyKey)
            .ToArrayAsync(cancellationToken);
    }

    internal async Task<PersistenceCounts> CountEffectsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        return new PersistenceCounts(
            await dbContext.Orders.CountAsync(cancellationToken),
            await dbContext.Incorporations.CountAsync(cancellationToken),
            await dbContext.IncorporationContents.CountAsync(cancellationToken),
            await dbContext.ConfirmationHistory.CountAsync(cancellationToken),
            await dbContext.FirstConfirmationCommands.CountAsync(cancellationToken),
            await dbContext.FirstConfirmationCommandContents.CountAsync(cancellationToken));
    }

    internal async Task<OrderOperationsSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        return new OrderOperationsSnapshot(
            await dbContext.Orders.AsNoTracking().SingleAsync(cancellationToken),
            await dbContext.Incorporations.AsNoTracking().SingleAsync(cancellationToken),
            await dbContext.IncorporationContents.AsNoTracking().ToArrayAsync(cancellationToken),
            await dbContext.ConfirmationHistory.AsNoTracking().SingleAsync(cancellationToken),
            await dbContext.FirstConfirmationCommands.AsNoTracking().SingleAsync(cancellationToken));
    }

    internal async Task SetHistoryFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();

        if (enabled)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION order_operations.fail_confirmation_history()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'controlled confirmation history failure';
                END;
                $$;
                CREATE TRIGGER fail_confirmation_history
                BEFORE INSERT ON order_operations.confirmation_history
                FOR EACH ROW EXECUTE FUNCTION order_operations.fail_confirmation_history();
                """,
                cancellationToken);
        }
        else
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER IF EXISTS fail_confirmation_history
                    ON order_operations.confirmation_history;
                DROP FUNCTION IF EXISTS order_operations.fail_confirmation_history();
                """,
                cancellationToken);
        }
    }

    internal async Task SetPreparationWorkFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();

        var sql = enabled
            ? """
              CREATE OR REPLACE FUNCTION order_operations.fail_preparation_work()
              RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN
                  RAISE EXCEPTION 'controlled preparation work failure';
              END;
              $$;
              CREATE TRIGGER fail_preparation_work
              BEFORE INSERT ON order_operations.preparation_work
              FOR EACH ROW EXECUTE FUNCTION order_operations.fail_preparation_work();
              """
            : """
              DROP TRIGGER IF EXISTS fail_preparation_work
                  ON order_operations.preparation_work;
              DROP FUNCTION IF EXISTS order_operations.fail_preparation_work();
              """;
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    internal async Task SetDeliveryStateFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();

        var sql = enabled
            ? """
              CREATE OR REPLACE FUNCTION order_operations.fail_delivery_state()
              RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN
                  RAISE EXCEPTION 'controlled delivery state failure';
              END;
              $$;
              CREATE TRIGGER fail_delivery_state
              BEFORE INSERT ON order_operations.delivery_states
              FOR EACH ROW EXECUTE FUNCTION order_operations.fail_delivery_state();
              """
            : """
              DROP TRIGGER IF EXISTS fail_delivery_state
                  ON order_operations.delivery_states;
              DROP FUNCTION IF EXISTS order_operations.fail_delivery_state();
              """;
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    internal async Task SetSubsequentCommandFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();

        if (enabled)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION order_operations.fail_subsequent_command()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'controlled subsequent command failure';
                END;
                $$;
                CREATE TRIGGER fail_subsequent_command
                BEFORE INSERT ON order_operations.subsequent_confirmation_commands
                FOR EACH ROW EXECUTE FUNCTION order_operations.fail_subsequent_command();
                """,
                cancellationToken);
        }
        else
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER IF EXISTS fail_subsequent_command
                    ON order_operations.subsequent_confirmation_commands;
                DROP FUNCTION IF EXISTS order_operations.fail_subsequent_command();
                """,
                cancellationToken);
        }
    }

    internal async Task SetPreparationCommandFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();

        if (enabled)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION order_operations.fail_preparation_command()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'controlled preparation command failure';
                END;
                $$;
                CREATE TRIGGER fail_preparation_command
                BEFORE INSERT ON order_operations.preparation_commands
                FOR EACH ROW EXECUTE FUNCTION order_operations.fail_preparation_command();
                """,
                cancellationToken);
        }
        else
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER IF EXISTS fail_preparation_command
                    ON order_operations.preparation_commands;
                DROP FUNCTION IF EXISTS order_operations.fail_preparation_command();
                """,
                cancellationToken);
        }
    }

    internal async Task SetPreparationHistoryFailureAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();

        var sql = enabled
            ? """
              CREATE OR REPLACE FUNCTION order_operations.fail_preparation_history()
              RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN
                  RAISE EXCEPTION 'controlled preparation history failure';
              END;
              $$;
              CREATE TRIGGER fail_preparation_history
              BEFORE INSERT ON order_operations.preparation_history
              FOR EACH ROW EXECUTE FUNCTION order_operations.fail_preparation_history();
              """
            : """
              DROP TRIGGER IF EXISTS fail_preparation_history
                  ON order_operations.preparation_history;
              DROP FUNCTION IF EXISTS order_operations.fail_preparation_history();
              """;
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    internal async Task<SubsequentPersistenceCounts> CountSubsequentEffectsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        return new SubsequentPersistenceCounts(
            await dbContext.Incorporations.CountAsync(cancellationToken),
            await dbContext.IncorporationContents.CountAsync(cancellationToken),
            await dbContext.ConfirmationHistory.CountAsync(cancellationToken),
            await dbContext.SubsequentConfirmationCommands.CountAsync(cancellationToken),
            await dbContext.SubsequentConfirmationCommandContents.CountAsync(cancellationToken));
    }

    internal async Task<bool> WaitForOrderRowLockWaitersAsync(
        int minimumWaiters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT count(*)
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query LIKE '%FROM order_operations.orders%FOR UPDATE%'
                """;
            var count = Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken));
            if (count >= minimumWaiters)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        return false;
    }

    internal async Task<bool> HasPendingModelChangesAsync()
    {
        await using var scope = application!.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>()
            .Database.HasPendingModelChanges();
    }

    internal async Task MigrateOrderOperationsAsync(
        string targetMigration,
        CancellationToken cancellationToken)
    {
        await using var scope = application!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        await dbContext.GetService<IMigrator>()
            .MigrateAsync(targetMigration, cancellationToken);
    }

    internal async Task RestartApplicationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Client.Dispose();
        await application!.DisposeAsync();
        StartApplication();
    }

    internal WebApplicationFactory<Program> CreateApplicationWithCatalog(
        IOrderConfirmationCatalog replacement) =>
        CreateApplication(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOrderConfirmationCatalog>();
                services.AddScoped(_ => replacement);
            }));

    internal WebApplicationFactory<Program> CreateApplicationWithCatalogDecorator(
        Func<IServiceProvider, IOrderConfirmationCatalog> factory) =>
        CreateApplication(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOrderConfirmationCatalog>();
                services.AddScoped(factory);
            }));

    internal WebApplicationFactory<Program> CreateApplicationWithProductLookupDecorator(
        Func<IServiceProvider, IProductOperationalReferenceLookup> factory) =>
        CreateApplication(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProductOperationalReferenceLookup>();
                services.AddScoped(factory);
            }));

    internal WebApplicationFactory<Program> CreateApplicationWithCapabilityDecorator(
        Func<IServiceProvider, IPreparationCapabilityStabilizer> factory) =>
        CreateApplication(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPreparationCapabilityStabilizer>();
                services.AddScoped(factory);
            }));

    internal WebApplicationFactory<Program> CreateApplicationWithTimeProvider(
        TimeProvider timeProvider) =>
        CreateApplication(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(timeProvider);
            }));

    internal WebApplicationFactory<Program>
        CreateApplicationWithCatalogDecoratorAndPreparationLookup(
            Func<IServiceProvider, IOrderConfirmationCatalog> catalogFactory,
            IPreparationResponsibilityLookup preparationLookup) =>
        CreateApplication(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOrderConfirmationCatalog>();
                services.AddScoped(catalogFactory);
                services.RemoveAll<IPreparationResponsibilityLookup>();
                services.AddScoped(_ => preparationLookup);
            }));

    internal async Task<bool> WaitForPriceUpdateLockAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND state = 'active'
                      AND wait_event_type = 'Lock'
                      AND query LIKE 'UPDATE catalog.products%')
                """;
            if (Assert.IsType<bool>(await command.ExecuteScalarAsync(cancellationToken)))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        return false;
    }

    internal async Task<bool> WaitForIdentityMutationLockAsync(
        string tableName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND state = 'active'
                      AND wait_event_type = 'Lock'
                      AND query ILIKE @query_pattern)
                """;
            command.Parameters.AddWithValue(
                "query_pattern",
                $"%identities_and_capabilities.{tableName}%");
            if (Assert.IsType<bool>(
                    await command.ExecuteScalarAsync(cancellationToken)))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (application is not null)
        {
            await application.DisposeAsync();
        }

        await postgres.DisposeAsync();
    }

    private void StartApplication()
    {
        application = CreateApplication();
        Client = application.CreateClient();
    }

    private WebApplicationFactory<Program> CreateApplication(
        Action<IWebHostBuilder>? configure = null)
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(
                    "ConnectionStrings:Catalog",
                    postgres.GetConnectionString());
                builder.UseSetting(
                    "ConnectionStrings:IdentitiesAndCapabilities",
                    postgres.GetConnectionString());
                builder.UseSetting(
                    "ConnectionStrings:OperationalConfiguration",
                    postgres.GetConnectionString());
                builder.UseSetting(
                    "ConnectionStrings:OrderOperations",
                    postgres.GetConnectionString());
                builder.UseSetting(
                    "NexoBarSecurity:Cookies:SessionName",
                    "nexobar-order-operations-session-test");
                builder.UseSetting(
                    "NexoBarSecurity:Cookies:AntiforgeryName",
                    "nexobar-order-operations-antiforgery-test");
                builder.UseSetting("NexoBarSecurity:Cookies:Secure", "false");
                configure?.Invoke(builder);
            });

        return factory;
    }
}

internal sealed record PersistenceCounts(
    int Orders,
    int Incorporations,
    int Contents,
    int History,
    int Commands,
    int CommandContents)
{
    internal static readonly PersistenceCounts Empty = new(0, 0, 0, 0, 0, 0);
}

internal sealed record OrderOperationsSnapshot(
    Order Order,
    Incorporation Incorporation,
    IReadOnlyList<IncorporationContent> Contents,
    ConfirmationHistory History,
    FirstConfirmationCommand Command);

internal sealed record SubsequentPersistenceCounts(
    int Incorporations,
    int Contents,
    int History,
    int Commands,
    int CommandContents);

internal sealed record PreparationWorkSnapshot(
    Guid Id,
    Guid IncorporationId,
    int ContentOrdinal,
    Guid ProductId,
    string? Instruction,
    Guid PreparationResponsibilityId,
    int TotalQuantity,
    int PendingQuantity,
    int InPreparationQuantity,
    int ReadyQuantity);

internal sealed record DeliveryStateSnapshot(
    Guid IncorporationId,
    int ContentOrdinal,
    Guid ProductId,
    string? Instruction,
    int DeliveredQuantity);

internal sealed record ConfirmedContentSnapshot(
    Guid OrderId,
    Guid IncorporationId,
    int IncorporationOrdinal,
    int ContentOrdinal,
    Guid ProductId,
    bool RequiresPreparationAtConfirmation);

internal sealed record PreparationActor(
    Guid IdentityId,
    string LoginIdentifier,
    string Secret);

[CollectionDefinition(Name)]
public sealed class OrderOperationsApiCollection :
    ICollectionFixture<OrderOperationsApiFixture>
{
    public const string Name = "OrderOperations API with PostgreSQL";
}
