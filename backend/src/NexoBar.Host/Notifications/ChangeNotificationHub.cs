using System.Collections.Concurrent;
using System.Threading.Channels;

namespace NexoBar.Host.Notifications;

internal sealed class ChangeNotificationHub : IChangeNotificationPublisher
{
    internal const int BufferCapacity = 16;
    private readonly ConcurrentDictionary<ChangeNotificationSubscription, byte> subscriptions = new();

    internal int SubscriptionCount => subscriptions.Count;

    // Only the transport registers snapshots, after authorization. The hub has no authority policy.
    internal ChangeNotificationSubscription Subscribe(IEnumerable<ChangeNotificationScope> scopes)
    {
        var subscription = new ChangeNotificationSubscription(this, scopes);
        subscriptions.TryAdd(subscription, 0);
        return subscription;
    }

    public void Publish(ChangeNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(notification.Scope);
        foreach (var subscription in subscriptions.Keys)
        {
            subscription.TryPublish(notification);
        }
    }

    internal void Remove(ChangeNotificationSubscription subscription) =>
        subscriptions.TryRemove(subscription, out _);
}

internal sealed class ChangeNotificationSubscription : IDisposable
{
    private readonly ChangeNotificationHub hub;
    private readonly HashSet<ChangeNotificationScope> scopes;
    private readonly HashSet<ChangeNotificationScope> retiredScopes = [];
    private readonly object gate = new();
    private readonly CancellationTokenSource dropped = new();
    private readonly Channel<ChangeNotification> channel = Channel.CreateBounded<ChangeNotification>(
        new BoundedChannelOptions(ChangeNotificationHub.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
    private bool stopped;
    private bool disposed;

    internal ChangeNotificationSubscription(
        ChangeNotificationHub hub, IEnumerable<ChangeNotificationScope> scopes)
    {
        this.hub = hub;
        this.scopes = scopes.ToHashSet();
        Dropped = dropped.Token;
    }

    internal ChannelReader<ChangeNotification> Reader => channel.Reader;
    internal CancellationToken Dropped { get; }
    internal int ScopeCount => scopes.Count;

    internal void Retire(ChangeNotificationScope scope)
    {
        lock (gate)
        {
            retiredScopes.Add(scope);
        }
    }

    internal void TryPublish(ChangeNotification notification)
    {
        lock (gate)
        {
            if (stopped || !scopes.Contains(notification.Scope) || retiredScopes.Contains(notification.Scope))
            {
                return;
            }

            // Never await channel capacity or client IO. Overflow retires the whole stream.
            if (!channel.Writer.TryWrite(notification))
            {
                Stop();
            }
        }
    }

    private void Stop()
    {
        stopped = true;
        hub.Remove(this);
        channel.Writer.TryComplete();
        // Cancellation can invoke transport callbacks. Do not run them on the publisher's thread.
        _ = dropped.CancelAsync();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (!stopped)
            {
                Stop();
            }

            dropped.Dispose();
            disposed = true;
        }
    }
}
