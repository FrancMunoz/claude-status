using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// The subject is internal, so it is exercised through the monitor that owns it.
/// </summary>
public class SnapshotSubscriptionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UsageMonitor Monitor(out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider(Fixture.FixedNow);
        return new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock),
            new PollingOptions { JitterFraction = 0d },
            clock,
            () => 0d);
    }

    [Fact]
    public void Disposing_the_monitor_completes_every_subscription()
    {
        UsageMonitor monitor = Monitor(out FakeTimeProvider _);
        var observer = new RecordingObserver();
        using IDisposable subscription = monitor.Snapshots.Subscribe(observer);

        monitor.Dispose();

        observer.Completed.Should().BeTrue();
    }

    [Fact]
    public void Subscribing_after_disposal_completes_immediately()
    {
        UsageMonitor monitor = Monitor(out FakeTimeProvider _);
        monitor.Dispose();

        var observer = new RecordingObserver();
        using IDisposable subscription = monitor.Snapshots.Subscribe(observer);

        observer.Completed.Should().BeTrue();
        observer.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_subscriber_receives_every_snapshot()
    {
        UsageMonitor monitor = Monitor(out FakeTimeProvider _);
        var first = new RecordingObserver();
        var second = new RecordingObserver();
        using IDisposable subscription = monitor.Snapshots.Subscribe(first);
        using IDisposable secondSubscription = monitor.Snapshots.Subscribe(second);

        await monitor.RefreshNowAsync(Ct);

        first.Received.Should().ContainSingle();
        second.Received.Should().ContainSingle();
        monitor.Dispose();
    }

    [Fact]
    public void Disposing_a_subscription_twice_is_safe()
    {
        UsageMonitor monitor = Monitor(out FakeTimeProvider _);
        IDisposable handle = monitor.Snapshots.Subscribe(new RecordingObserver());

        handle.Dispose();
        Action act = handle.Dispose;

        act.Should().NotThrow();
        monitor.Dispose();
    }

    [Fact]
    public void Rejects_a_null_observer()
    {
        UsageMonitor monitor = Monitor(out FakeTimeProvider _);

        FluentActions.Invoking(() => monitor.Snapshots.Subscribe(null!))
            .Should().Throw<ArgumentNullException>();

        monitor.Dispose();
    }

    [Fact]
    public void Completing_twice_is_safe()
    {
        UsageMonitor monitor = Monitor(out FakeTimeProvider _);
        var observer = new RecordingObserver();
        using IDisposable subscription = monitor.Snapshots.Subscribe(observer);

        monitor.Dispose();
        monitor.Dispose();

        observer.CompletedCount.Should().Be(1, "completion must not be announced twice");
    }

    private sealed class RecordingObserver : IObserver<UsageSnapshot>
    {
        public List<UsageSnapshot> Received { get; } = [];

        public int CompletedCount { get; private set; }

        public bool Completed => CompletedCount > 0;

        public void OnCompleted() => CompletedCount++;

        public void OnError(Exception error)
        {
        }

        public void OnNext(UsageSnapshot value) => Received.Add(value);
    }
}
