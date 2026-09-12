using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexoBar.Host.Notifications;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

public sealed class SseLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_or_application_shutdown_completes_and_unsubscribes(bool shutdown)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var lifetime = new TestLifetime();
        await using var body = new TestBody(false);
        await using var services = CreateServices();
        var hub = new ChangeNotificationHub();
        var context = CreateContext(body, cancellation.Token);
        var running = RunAsync(context, hub, lifetime, services);
        await body.Writing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, hub.SubscriptionCount);
        if (shutdown) lifetime.StopApplication();
        else await cancellation.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, hub.SubscriptionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_network_write_is_cancelled_by_overflow_or_write_deadline(bool overflow)
    {
        using var lifetime = new TestLifetime();
        await using var body = new TestBody(true);
        await using var services = CreateServices();
        var hub = new ChangeNotificationHub();
        var context = CreateContext(body, TestContext.Current.CancellationToken);
        var running = RunAsync(context, hub, lifetime, services,
            overflow ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100));
        await body.Writing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (overflow)
        {
            var scope = ChangeNotificationScope.PreparationDestination(Destination);
            for (var count = 0; count <= ChangeNotificationHub.BufferCapacity; count++)
            {
                hub.Publish(new ChangeNotification(scope));
            }
        }
        await running.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(0, hub.SubscriptionCount);
    }

    private static readonly Guid Destination = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ServiceProvider CreateServices() => new ServiceCollection()
        .AddLogging()
        .AddScoped<IPreparationSubscriptionAuthorization, Authorized>()
        .BuildServiceProvider();

    private static DefaultHttpContext CreateContext(Stream body, CancellationToken cancellationToken)
    {
        var context = new DefaultHttpContext { RequestAborted = cancellationToken };
        context.Request.QueryString = new QueryString($"?scope=preparation.destination:{Destination:D}");
        context.Response.Body = body;
        return context;
    }

    private static Task RunAsync(HttpContext context, ChangeNotificationHub hub,
        IHostApplicationLifetime lifetime, IServiceProvider services, TimeSpan? writeTimeout = null) =>
        SseTransport.StreamAsync(context, hub, services.GetRequiredService<IServiceScopeFactory>(),
            lifetime, Options.Create(new SseTransportOptions
            {
                WriteTimeout = writeTimeout ?? TimeSpan.FromSeconds(5)
            }), TimeProvider.System, services.GetRequiredService<ILoggerFactory>());

    private sealed class Authorized : IPreparationSubscriptionAuthorization
    {
        public Task<PreparationAuthorizationOutcome> AuthorizeAsync(
            IReadOnlyCollection<Guid> destinationIds, CancellationToken cancellationToken) =>
            Task.FromResult(PreparationAuthorizationOutcome.Authorized);
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopping.Token;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }

    private sealed class TestBody(bool block) : MemoryStream
    {
        internal TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Writing.TrySetResult();
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
