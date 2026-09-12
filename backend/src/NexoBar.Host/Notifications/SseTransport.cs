using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Host.Notifications;

internal sealed class SseTransportOptions
{
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

internal static class SseTransport
{
    internal const int MaximumScopes = 8;

    internal static IServiceCollection AddSseTransport(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SseTransportOptions>()
            .Bind(configuration.GetSection("Sse"))
            .Validate(options => options.HeartbeatInterval > TimeSpan.Zero &&
                options.HeartbeatInterval <= TimeSpan.FromMinutes(1) &&
                options.WriteTimeout > TimeSpan.Zero &&
                options.WriteTimeout <= TimeSpan.FromSeconds(30),
                "SSE heartbeat/write intervals must be positive and bounded.")
            .ValidateOnStart();
        services.AddSingleton<ChangeNotificationHub>();
        services.AddSingleton<IChangeNotificationPublisher>(provider =>
            provider.GetRequiredService<ChangeNotificationHub>());
        return services;
    }

    internal static void MapSseTransport(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/notifications/stream", StreamAsync)
            .RequireAuthorization()
            .WithMetadata(new PassiveSessionRequest())
            .WithTags("Notifications")
            .WithName("StreamChangeNotifications")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

    internal static async Task StreamAsync(
        HttpContext context,
        ChangeNotificationHub hub,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        IOptions<SseTransportOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        var rawScopes = context.Request.Query["scope"];
        var scopes = new HashSet<ChangeNotificationScope>();
        if (rawScopes.Count is 0 or > MaximumScopes ||
            rawScopes.Any(raw => !ChangeNotificationScope.TryParse(raw, out _)))
        {
            await Problem(400, "Invalid subscription", "sse.invalid_subscription").ExecuteAsync(context);
            return;
        }

        foreach (var raw in rawScopes)
        {
            ChangeNotificationScope.TryParse(raw, out var scope);
            scopes.Add(scope!);
        }

        var destinationIds = scopes.Select(scope => scope.DestinationId).ToArray();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, lifetime.ApplicationStopping);
        var logger = loggerFactory.CreateLogger("NexoBar.SseTransport");
        try
        {
            var initial = await AuthorizeAsync(scopeFactory, destinationIds, connection.Token);
            if (initial != PreparationAuthorizationOutcome.Authorized)
            {
                await (initial == PreparationAuthorizationOutcome.Unauthenticated
                    ? Problem(401, "Invalid session", "identities_and_capabilities.invalid_session")
                    : Problem(403, "Preparation access forbidden", "identities_and_capabilities.preparation.forbidden"))
                    .ExecuteAsync(context);
                return;
            }

            using var subscription = hub.Subscribe(scopes);
            using var droppedRegistration = subscription.Dropped.Register(connection.Cancel);
            using var timer = new PeriodicTimer(options.Value.HeartbeatInterval, timeProvider);
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store, no-cache, no-transform";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            // Flush immediately so EventSource opens even when no notifications exist.
            await WriteAsync(context.Response, ": connected\n\n", options.Value, connection.Token);

            var readable = subscription.Reader.WaitToReadAsync(connection.Token).AsTask();
            var heartbeat = timer.WaitForNextTickAsync(connection.Token).AsTask();
            while (!connection.IsCancellationRequested)
            {
                await Task.WhenAny(readable, heartbeat);
                var isHeartbeat = heartbeat.IsCompleted;
                if (isHeartbeat && !await heartbeat || !isHeartbeat && !await readable)
                {
                    break;
                }

                // Short, fresh authorization transaction, never held across network writes.
                if (await AuthorizeAsync(scopeFactory, destinationIds, connection.Token) !=
                    PreparationAuthorizationOutcome.Authorized)
                {
                    logger.LogInformation("SSE connection authority lost.");
                    break;
                }

                connection.Token.ThrowIfCancellationRequested();
                if (isHeartbeat)
                {
                    await WriteAsync(context.Response, ": keep-alive\n\n", options.Value, connection.Token);
                    heartbeat = timer.WaitForNextTickAsync(connection.Token).AsTask();
                }
                else
                {
                    if (subscription.Reader.TryRead(out var notification))
                    {
                        var json = JsonSerializer.Serialize(new
                        {
                            kind = notification.Kind,
                            scopeId = notification.Scope.DestinationId
                        });
                        await WriteAsync(context.Response, $"event: invalidation\ndata: {json}\n\n",
                            options.Value, connection.Token);
                    }

                    readable = subscription.Reader.WaitToReadAsync(connection.Token).AsTask();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Request/shutdown/overflow cancellation or bounded write timeout.
            if (context.Response.HasStarted)
            {
                context.Abort();
            }
        }
        catch (IOException)
        {
            // Disconnected peer: disposal removes the subscription.
            context.Abort();
        }
        catch (Exception exception) when (context.Response.HasStarted)
        {
            logger.LogWarning(exception, "SSE connection terminated after transport or authority check failure.");
            context.Abort();
        }
        finally
        {
            await connection.CancelAsync();
        }
    }

    private static async Task<PreparationAuthorizationOutcome> AuthorizeAsync(
        IServiceScopeFactory scopeFactory, Guid[] destinationIds, CancellationToken cancellationToken)
    {
        // Avoid retaining a tracked DbContext/transaction for a long-lived response.
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPreparationSubscriptionAuthorization>()
            .AuthorizeAsync(destinationIds, cancellationToken);
    }

    private static async Task WriteAsync(
        HttpResponse response, string frame, SseTransportOptions options, CancellationToken cancellationToken)
    {
        using var write = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        write.CancelAfter(options.WriteTimeout);
        await response.WriteAsync(frame, write.Token);
        await response.Body.FlushAsync(write.Token);
    }

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
