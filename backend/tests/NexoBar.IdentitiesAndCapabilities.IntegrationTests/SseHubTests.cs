using NexoBar.Host.Notifications;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

public sealed class SseHubTests
{
    [Fact]
    public void Exact_scopes_filter_and_duplicate_snapshot_entries_are_deduplicated()
    {
        var hub = new ChangeNotificationHub();
        var kitchen = ChangeNotificationScope.PreparationDestination(Guid.NewGuid());
        var bar = ChangeNotificationScope.PreparationDestination(Guid.NewGuid());
        using var subscriber = hub.Subscribe([kitchen, kitchen]);
        Assert.Equal(1, subscriber.ScopeCount);
        hub.Publish(new ChangeNotification(bar));
        Assert.False(subscriber.Reader.TryRead(out _));
        hub.Publish(new ChangeNotification(kitchen));
        hub.Publish(new ChangeNotification(kitchen));
        Assert.True(subscriber.Reader.TryRead(out var first));
        Assert.True(subscriber.Reader.TryRead(out var second));
        Assert.Equal(first, second);
        Assert.False(subscriber.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Overflow_drops_slow_connection_without_blocking_publisher_or_healthy_connection()
    {
        var token = TestContext.Current.CancellationToken;
        var hub = new ChangeNotificationHub();
        var scope = ChangeNotificationScope.PreparationDestination(Guid.NewGuid());
        using var slow = hub.Subscribe([scope]);
        using var healthy = hub.Subscribe([scope]);
        await Task.Run(() =>
        {
            for (var count = 0; count <= ChangeNotificationHub.BufferCapacity; count++)
            {
                hub.Publish(new ChangeNotification(scope));
                Assert.True(healthy.Reader.TryRead(out _));
            }
        }, token).WaitAsync(TimeSpan.FromSeconds(2), token);
        Assert.True(slow.Dropped.IsCancellationRequested);
        Assert.False(healthy.Dropped.IsCancellationRequested);
        Assert.Equal(1, hub.SubscriptionCount);
        var buffered = 0;
        while (slow.Reader.TryRead(out _)) buffered++;
        Assert.Equal(ChangeNotificationHub.BufferCapacity, buffered);
        await slow.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(2), token);
        hub.Publish(new ChangeNotification(scope));
        Assert.True(healthy.Reader.TryRead(out _));
        Assert.False(slow.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Overflow_never_runs_slow_transport_cancellation_callback_on_publisher_thread()
    {
        var token = TestContext.Current.CancellationToken;
        var hub = new ChangeNotificationHub();
        var scope = ChangeNotificationScope.PreparationDestination(Guid.NewGuid());
        using var subscription = hub.Subscribe([scope]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = subscription.Dropped.Register(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        try
        {
            await Task.Run(() =>
            {
                for (var count = 0; count <= ChangeNotificationHub.BufferCapacity; count++)
                {
                    hub.Publish(new ChangeNotification(scope));
                }
            }, token).WaitAsync(TimeSpan.FromSeconds(2), token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
            Assert.Equal(0, hub.SubscriptionCount);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Disposing_subscription_unblocks_reader_and_removes_it_idempotently()
    {
        var hub = new ChangeNotificationHub();
        var scope = ChangeNotificationScope.PreparationDestination(Guid.NewGuid());
        var subscription = hub.Subscribe([scope]);
        var pending = subscription.Reader.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask();
        subscription.Dispose();
        subscription.Dispose();
        Assert.True(subscription.Dropped.IsCancellationRequested);
        Assert.False(await pending);
        Assert.Equal(0, hub.SubscriptionCount);
        hub.Publish(new ChangeNotification(scope));
    }
}
