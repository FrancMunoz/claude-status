using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Timing is driven by <see cref="FakeTimeProvider"/> and jitter by a fixed
/// function, so nothing here sleeps or flakes.
/// </summary>
public class UsageMonitorTests
{
    private static readonly PollingOptions FastOptions = new()
    {
        BaseInterval = TimeSpan.FromSeconds(60),
        MaxInterval = TimeSpan.FromSeconds(300),
        ForcedRefreshCooldown = TimeSpan.FromSeconds(30),
        JitterFraction = 0d,
    };

    private static FakeTimeProvider Clock() => new(Fixture.FixedNow);

    /// <summary>The ambient test cancellation token, so a hung test is cancellable.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RefreshNowAsync_publishes_a_snapshot()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), FastOptions, clock, () => 0d);

        List<UsageSnapshot> received = [];
        using IDisposable _ = monitor.Snapshots.Subscribe(new CollectingObserver(received));

        bool ran = await monitor.RefreshNowAsync(Ct);

        ran.Should().BeTrue();
        received.Should().ContainSingle();
        monitor.Latest!.Session!.Percent.Should().Be(29d);
        monitor.Status.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshNowAsync_is_rate_limited_so_a_user_cannot_get_us_throttled()
    {
        FakeTimeProvider clock = Clock();
        var provider = new FakeUsageProvider(FakeUsageScenario.Healthy, clock);
        using var monitor = new UsageMonitor(provider, FastOptions, clock, () => 0d);

        (await monitor.RefreshNowAsync(Ct)).Should().BeTrue();
        (await monitor.RefreshNowAsync(Ct)).Should().BeFalse("the cooldown has not elapsed");

        provider.FetchCount.Should().Be(1);
    }

    [Fact]
    public async Task RefreshNowAsync_is_allowed_again_once_the_cooldown_elapses()
    {
        FakeTimeProvider clock = Clock();
        var provider = new FakeUsageProvider(FakeUsageScenario.Healthy, clock);
        using var monitor = new UsageMonitor(provider, FastOptions, clock, () => 0d);

        await monitor.RefreshNowAsync(Ct);
        clock.Advance(FastOptions.ForcedRefreshCooldown + TimeSpan.FromSeconds(1));

        (await monitor.RefreshNowAsync(Ct)).Should().BeTrue();
        provider.FetchCount.Should().Be(2);
    }

    [Fact]
    public async Task A_failed_fetch_never_throws_out_of_the_monitor()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.AlwaysFails, clock), FastOptions, clock, () => 0d);

        bool ran = await monitor.RefreshNowAsync(Ct);

        ran.Should().BeTrue();
        monitor.Status.IsHealthy.Should().BeFalse();
        monitor.Status.Failure.Should().Be(UsageFetchFailure.Network);
    }

    [Fact]
    public async Task A_failure_after_a_success_republishes_the_last_reading_marked_stale()
    {
        FakeTimeProvider clock = Clock();
        var provider = new SwitchableProvider(clock);
        using var monitor = new UsageMonitor(provider, FastOptions, clock, () => 0d);

        List<UsageSnapshot> received = [];
        using IDisposable _ = monitor.Snapshots.Subscribe(new CollectingObserver(received));

        await monitor.RefreshNowAsync(Ct);
        provider.ShouldFail = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RefreshNowAsync(Ct);

        received.Should().HaveCount(2);
        received[0].IsStale.Should().BeFalse();
        received[1].IsStale.Should().BeTrue();
        received[1].Session!.Percent.Should().Be(29d, "the values are kept, only the freshness changes");
    }

    [Fact]
    public async Task Latest_stays_null_when_the_very_first_fetch_fails()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.AlwaysFails, clock), FastOptions, clock, () => 0d);

        await monitor.RefreshNowAsync(Ct);

        monitor.Latest.Should().BeNull("there is no cached reading to fall back to");
    }

    [Fact]
    public void SeedFrom_makes_a_cached_reading_available_immediately_and_marks_it_stale()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), FastOptions, clock, () => 0d);

        UsageSnapshot cached = Fixture.Parse(Fixture.Normal);
        monitor.SeedFrom(cached);

        monitor.Latest.Should().NotBeNull();
        monitor.Latest!.IsStale.Should().BeTrue();
        monitor.Latest.Session!.Percent.Should().Be(29d);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(4, 300)]
    [InlineData(10, 300)]
    public void Backoff_doubles_then_holds_at_the_ceiling(int failures, int expectedSeconds)
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), FastOptions, clock, () => 0d);

        monitor.NextDelayAfterFailure(failures, null)
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Backoff_never_drops_below_the_base_interval_even_with_full_jitter()
    {
        FakeTimeProvider clock = Clock();
        var options = FastOptions with { JitterFraction = 1d };
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), options, clock, () => 1d);

        monitor.NextDelayAfterFailure(1, null).Should().BeGreaterThanOrEqualTo(options.BaseInterval);
        monitor.NextDelayAfterFailure(5, null).Should().BeGreaterThanOrEqualTo(options.BaseInterval);
    }

    [Fact]
    public void Jitter_shortens_the_delay_within_the_configured_band()
    {
        FakeTimeProvider clock = Clock();
        var options = FastOptions with { JitterFraction = 0.5d };
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), options, clock, () => 1d);

        // Third failure: 60 * 4 = 240 s, minus 50 % jitter = 120 s.
        monitor.NextDelayAfterFailure(3, null).Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void A_server_Retry_After_wins_when_it_is_longer_than_our_own_curve()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), FastOptions, clock, () => 0d);

        // The real endpoint returned Retry-After: 1731 on a 429.
        monitor.NextDelayAfterFailure(1, TimeSpan.FromSeconds(1731))
            .Should().Be(TimeSpan.FromSeconds(1731));
    }

    [Fact]
    public void A_absurd_Retry_After_is_capped_so_the_app_cannot_be_parked_indefinitely()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), FastOptions, clock, () => 0d);

        monitor.NextDelayAfterFailure(1, TimeSpan.FromHours(9))
            .Should().Be(FastOptions.Normalized().MaxRetryAfter);
    }

    [Fact]
    public void A_short_Retry_After_does_not_shorten_our_backoff()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), FastOptions, clock, () => 0d);

        monitor.NextDelayAfterFailure(3, TimeSpan.FromSeconds(5))
            .Should().Be(TimeSpan.FromSeconds(240));
    }

    [Fact]
    public async Task A_success_after_failures_resets_the_backoff_to_the_base_interval()
    {
        FakeTimeProvider clock = Clock();
        var provider = new SwitchableProvider(clock) { ShouldFail = true };
        using var monitor = new UsageMonitor(provider, FastOptions, clock, () => 0d);

        await monitor.RefreshNowAsync(Ct);
        clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RefreshNowAsync(Ct);
        monitor.Status.NextPollDelay.Should().BeGreaterThan(FastOptions.BaseInterval);

        provider.ShouldFail = false;
        clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RefreshNowAsync(Ct);

        monitor.Status.NextPollDelay.Should().Be(FastOptions.BaseInterval);
        monitor.Status.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task Start_polls_repeatedly_and_Stop_ends_it()
    {
        FakeTimeProvider clock = Clock();
        var provider = new FakeUsageProvider(FakeUsageScenario.Healthy, clock);
        using var monitor = new UsageMonitor(provider, FastOptions, clock, () => 0d);

        monitor.Start();
        await WaitFor(() => provider.FetchCount >= 1);

        // The loop registers its timer only once it reaches Task.Delay, and Start()
        // is fire-and-forget. Advancing a single time races that registration, so
        // keep nudging the clock until the second poll lands.
        await AdvanceUntil(clock, FastOptions.BaseInterval, () => provider.FetchCount >= 2);

        monitor.Stop();
        int countAtStop = provider.FetchCount;
        clock.Advance(FastOptions.BaseInterval * 3);
        await Task.Delay(50, Ct);

        provider.FetchCount.Should().Be(countAtStop, "polling stopped");
    }

    [Fact]
    public void Start_is_idempotent()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), FastOptions, clock, () => 0d);

        monitor.Start();
        monitor.Start();

        monitor.IsRunning.Should().BeTrue();
        monitor.Stop();
    }

    [Fact]
    public void Stop_without_Start_does_nothing()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), FastOptions, clock, () => 0d);

        Action act = monitor.Stop;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task An_unauthorized_failure_is_surfaced_as_such_so_the_UI_can_offer_Config()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Unauthorized, clock), FastOptions, clock, () => 0d);

        await monitor.RefreshNowAsync(Ct);

        monitor.Status.Failure.Should().Be(UsageFetchFailure.Unauthorized);
    }

    [Fact]
    public async Task A_rate_limit_failure_honours_the_providers_Retry_After()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.RateLimited, clock), FastOptions, clock, () => 0d);

        await monitor.RefreshNowAsync(Ct);

        monitor.Status.Failure.Should().Be(UsageFetchFailure.RateLimited);
        monitor.Status.NextPollDelay.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_break_polling()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), FastOptions, clock, () => 0d);

        using IDisposable _ = monitor.Snapshots.Subscribe(new ThrowingObserver());
        List<UsageSnapshot> received = [];
        using IDisposable __ = monitor.Snapshots.Subscribe(new CollectingObserver(received));

        await monitor.RefreshNowAsync(Ct);

        received.Should().ContainSingle("the second subscriber still gets its snapshot");
        monitor.Status.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task An_unsubscribed_observer_stops_receiving()
    {
        FakeTimeProvider clock = Clock();
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), FastOptions, clock, () => 0d);

        List<UsageSnapshot> received = [];
        IDisposable subscription = monitor.Snapshots.Subscribe(new CollectingObserver(received));
        await monitor.RefreshNowAsync(Ct);
        subscription.Dispose();

        clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RefreshNowAsync(Ct);

        received.Should().ContainSingle();
    }

    [Fact]
    public async Task Disposing_twice_is_safe()
    {
        FakeTimeProvider clock = Clock();
        var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), FastOptions, clock, () => 0d);

        await monitor.RefreshNowAsync(Ct);
        monitor.Dispose();
        Action act = monitor.Dispose;

        act.Should().NotThrow();
    }

    /// <summary>
    /// Advances a fake clock repeatedly until <paramref name="condition"/> holds.
    /// </summary>
    /// <remarks>
    /// A single Advance races the loop task registering its timer. Nudging
    /// repeatedly is deterministic in outcome and still needs no real waiting.
    /// </remarks>
    private static async Task AdvanceUntil(
        FakeTimeProvider clock, TimeSpan step, Func<bool> condition, int timeoutMs = 10000)
    {
        for (int elapsed = 0; elapsed < timeoutMs; elapsed += 10)
        {
            if (condition())
            {
                return;
            }

            clock.Advance(step);
            await Task.Delay(10, Ct);
        }

        throw new TimeoutException("The expected condition did not occur in time.");
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 10000)
    {
        for (int elapsed = 0; elapsed < timeoutMs; elapsed += 10)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException("The expected condition did not occur in time.");
    }

    /// <summary>A provider whose success or failure can be flipped mid-test.</summary>
    private sealed class SwitchableProvider(TimeProvider clock) : IUsageProvider
    {
        private readonly FakeUsageProvider _inner = new(FakeUsageScenario.Healthy, clock);

        public bool ShouldFail { get; set; }

        public string NameKey => "Switchable";

        public Task<UsageSnapshot> FetchAsync(CancellationToken ct)
            => ShouldFail
                ? Task.FromException<UsageSnapshot>(
                    new UsageFetchException(UsageFetchFailure.Network, "simulated"))
                : _inner.FetchAsync(ct);
    }

    private sealed class CollectingObserver(List<UsageSnapshot> target) : IObserver<UsageSnapshot>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(UsageSnapshot value) => target.Add(value);
    }

    private sealed class ThrowingObserver : IObserver<UsageSnapshot>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(UsageSnapshot value) => throw new InvalidOperationException("badly behaved subscriber");
    }
}
