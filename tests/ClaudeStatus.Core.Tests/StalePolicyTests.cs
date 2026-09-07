namespace ClaudeStatus.Core.Tests;

/// <summary>
/// When a reading is old enough to be shown as out of date.
/// </summary>
/// <remarks>
/// The rule every indicator shares, on every platform. It exists because
/// <see cref="UsageSnapshot.IsStale"/> answers a different question - "did the last
/// refresh work" - and painting with that answer against an endpoint which 429s
/// readily left the indicator faded over readings that were seconds old.
/// </remarks>
public class StalePolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static UsageSnapshot Snapshot(bool stale, DateTimeOffset? fetchedAt = null) => new(
        UsageWindow.Create(42d, Now),
        UsageWindow.Create(18d, Now),
        null,
        new Dictionary<string, UsageWindow>(),
        fetchedAt ?? Now,
        stale);

    [Fact]
    public void A_flagged_reading_that_is_seconds_old_is_not_shown_as_stale()
    {
        // The case the whole policy exists for.
        StalePolicy.ShowsAsStale(
            Snapshot(stale: true),
            Now + TimeSpan.FromSeconds(40),
            StalePolicy.Floor)
            .Should().BeFalse();
    }

    [Fact]
    public void A_flagged_reading_past_the_threshold_is_shown_as_stale()
    {
        StalePolicy.ShowsAsStale(
            Snapshot(stale: true),
            Now + TimeSpan.FromMinutes(10),
            StalePolicy.Floor)
            .Should().BeTrue();
    }

    [Fact]
    public void An_old_reading_the_monitor_is_happy_with_is_not_shown_as_stale()
    {
        // Age alone would fade a perfectly good reading in the gap between a slow
        // poll and its reply.
        StalePolicy.ShowsAsStale(
            Snapshot(stale: false),
            Now + TimeSpan.FromHours(2),
            StalePolicy.Floor)
            .Should().BeFalse();
    }

    [Fact]
    public void A_reading_that_never_arrived_is_not_shown_as_stale()
    {
        StalePolicy.ShowsAsStale(null, Now, StalePolicy.Floor).Should().BeFalse();
    }

    [Fact]
    public void The_threshold_scales_with_how_often_a_reading_is_expected()
    {
        // A long interval means an old reading is normal, so fading one that
        // arrived on schedule would mark every reading doubtful.
        StalePolicy.ThresholdFor(TimeSpan.FromMinutes(15))
            .Should().Be(TimeSpan.FromMinutes(45));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(1)]
    public void A_missing_or_absurd_interval_falls_back_to_the_floor(int? seconds)
    {
        // Never zero. A threshold of nothing would fade every reading the instant
        // one request failed, which is the behaviour this replaced.
        TimeSpan? interval = seconds is { } value ? TimeSpan.FromSeconds(value) : null;

        StalePolicy.ThresholdFor(interval).Should().Be(StalePolicy.Floor);
    }

    [Fact]
    public void The_default_poll_interval_tolerates_a_failed_poll_and_its_retry()
    {
        // The default is a minute, and a failure retries a minute later. The
        // threshold has to outlast both or the fade returns on every hiccup.
        TimeSpan threshold = StalePolicy.ThresholdFor(TimeSpan.FromMinutes(1));

        threshold.Should().BeGreaterThan(TimeSpan.FromMinutes(2));
    }
}
