using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OrderOperations;

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
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

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
        var retiredScopes = new HashSet<ChangeNotificationScope>();
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

        using var connection = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, lifetime.ApplicationStopping);
        var logger = loggerFactory.CreateLogger("NexoBar.SseTransport");
        try
        {
            var initial = await AuthorizeAsync(scopeFactory, scopes, retiredScopes, null, connection.Token);
            if (initial is not null)
            {
                await initial.ExecuteAsync(context);
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
                // Inspect a queued notification before heartbeat revalidation: an explicit
                // terminal-final notification has a different (actor-only) Order boundary.
                var isHeartbeat = !readable.IsCompleted && heartbeat.IsCompleted;
                if (isHeartbeat && !await heartbeat || !isHeartbeat && !await readable)
                {
                    break;
                }

                ChangeNotification? notification = null;
                if (!isHeartbeat && !subscription.Reader.TryRead(out notification))
                {
                    readable = subscription.Reader.WaitToReadAsync(connection.Token).AsTask();
                    continue;
                }
                if (notification is not null && retiredScopes.Contains(notification.Scope))
                {
                    readable = subscription.Reader.WaitToReadAsync(connection.Token).AsTask();
                    continue;
                }
                var isFinal = notification?.Delivery == ChangeNotificationDelivery.FinalForPreviouslyAuthorizedScope;
                if (isFinal && (notification!.Scope.Kind != ChangeNotificationScopeKind.ActiveOrder ||
                    !scopes.Contains(notification.Scope))) break;

                // Short, fresh authorization transactions, never held across network writes.
                // The exact final scope and already-retired scopes skip active State;
                // remaining deliverable scopes and all actor authority are still current.
                if (await AuthorizeAsync(scopeFactory, scopes, retiredScopes,
                        isFinal ? notification!.Scope.ScopeId : null, connection.Token) is not null)
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
                    var json = JsonSerializer.Serialize(new
                    {
                        kind = notification!.Kind,
                        scopeId = notification.Scope.ScopeId
                    });
                    await WriteAsync(context.Response, $"event: invalidation\ndata: {json}\n\n",
                        options.Value, connection.Token);
                    // Preserve the client snapshot, but permanently retire this exact scope.
                    // The hub rejects future publications; the reader also skips queued ones.
                    if (isFinal)
                    {
                        retiredScopes.Add(notification.Scope);
                        subscription.Retire(notification.Scope);
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

    private static async Task<IResult?> AuthorizeAsync(
        IServiceScopeFactory scopeFactory, IReadOnlyCollection<ChangeNotificationScope> scopes,
        IReadOnlySet<ChangeNotificationScope> retiredScopes,
        Guid? finalOrderId, CancellationToken cancellationToken)
    {
        // Avoid retaining a tracked DbContext/transaction for a long-lived response.
        await using var scope = scopeFactory.CreateAsyncScope();
        var destinations = scopes.Where(x => x.Kind == ChangeNotificationScopeKind.PreparationDestination)
            .Select(x => x.ScopeId).ToArray();
        if (destinations.Length > 0)
        {
            var preparation = await scope.ServiceProvider.GetRequiredService<IPreparationSubscriptionAuthorization>()
                .AuthorizeAsync(destinations, cancellationToken);
            if (preparation != PreparationAuthorizationOutcome.Authorized)
                return preparation == PreparationAuthorizationOutcome.Unauthenticated
                    ? Problem(401, "Invalid session", "identities_and_capabilities.invalid_session")
                    : Problem(403, "Preparation access forbidden", "identities_and_capabilities.preparation.forbidden");
        }
        var orders = scopes.Where(x => x.Kind == ChangeNotificationScopeKind.ActiveOrder).Select(x => x.ScopeId).ToArray();
        if (orders.Length == 0) return null;
        var authorization = scope.ServiceProvider.GetRequiredService<IActiveOrderSubscriptionAuthorization>();
        var activeOrders = orders.Where(id => id != finalOrderId &&
            !retiredScopes.Contains(ChangeNotificationScope.ActiveOrder(id))).ToArray();
        var outcome = activeOrders.Length == 0
            ? await authorization.AuthorizeCurrentActorAsync(cancellationToken)
            : await authorization.AuthorizeAsync(activeOrders, cancellationToken);
        return outcome switch
        {
            ActiveOrderSubscriptionAuthorizationOutcome.Authorized => null,
            ActiveOrderSubscriptionAuthorizationOutcome.Unauthenticated => Problem(401, "Invalid session", "identities_and_capabilities.invalid_session"),
            ActiveOrderSubscriptionAuthorizationOutcome.Forbidden => Problem(403, "Order access forbidden", "order_operations.order.forbidden"),
            ActiveOrderSubscriptionAuthorizationOutcome.OrderNotFound => Problem(404, "Order not found", "order_operations.order.not_found"),
            _ => throw new InvalidOperationException("Unknown active Order authorization outcome.")
        };
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
