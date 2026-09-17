using ClaudeStatus.Usage;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// The part of the velocity warning that watches snapshots go by: which window
/// wins, what a stale reading does, and when the whole thing stays quiet.
/// </summary>
public class VelocityTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Feeds a minute-by-minute climb into the tracker and returns its verdict.</summary>
    /// <param name="sessionFrom">Where the session starts.</param>
    /// <param name="sessionRate">Session climb, percent per hour.</param>
    /// <param name="weekFrom">Where the week starts.</param>
    /// <param name="weekRate">Week climb, percent per hour.</param>
    /// <param name="minutes">How long to run for.</param>
    /// <param name="sessionResetsIn">Session reset, from the start.</param>
    /// <param name="weekResetsIn">Week reset, from the start.</param>
    /// <param name="fable">Fable's reading, when the plan has one.</param>
    /// <param name="enabled">Whether the user wants these warnings.</param>
    private static VelocityAlert? Run(
        double sessionFrom,
        double sessionRate,
        double weekFrom,
        double weekRate,
        int minutes,
        TimeSpan sessionResetsIn,
        TimeSpan weekResetsIn,
        double? fable = null,
        bool enabled = true)
    {
        var tracker = new VelocityTracker();
        VelocityAlert? alert = null;

        for (int m = 0; m <= minutes; m++)
        {
            DateTimeOffset at = T0.AddMinutes(m);
            var snapshot = new UsageSnapshot(
                new UsageWindow(sessionFrom + (sessionRate * m / 60d), T0 + sessionResetsIn),
                new UsageWindow(weekFrom + (weekRate * m / 60d), T0 + weekResetsIn),
                fable is { } f ? new UsageWindow(f, T0 + weekResetsIn) : null,
                new Dictionary<string, UsageWindow>(),
                at,
                IsStale: false);

            alert = tracker.Observe(snapshot, at, enabled);
        }

        return alert;
    }

    [Fact]
    public void A_fast_session_raises_a_session_alert()
    {
        VelocityAlert? alert = Run(
            sessionFrom: 40,
            sessionRate: 60,
            weekFrom: 20,
            weekRate: 1,
            minutes: 10,
            sessionResetsIn: TimeSpan.FromHours(2),
            weekResetsIn: TimeSpan.FromDays(5));

        alert.Should().NotBeNull();
        alert!.Window.Should().Be(VelocityWindow.Session);
    }

    [Fact]
    public void The_window_that_runs_out_first_is_the_one_reported()
    {
        // Both are in trouble. The session is the more urgent, and one warning is
        // all there is room for.
        VelocityAlert? alert = Run(
            sessionFrom: 40,
            sessionRate: 60,
            weekFrom: 60,
            weekRate: 20,
            minutes: 30,
            sessionResetsIn: TimeSpan.FromHours(4),
            weekResetsIn: TimeSpan.FromDays(3));

        alert.Should().NotBeNull();
        alert!.Window.Should().Be(VelocityWindow.Session);
        alert.UntilExhausted.Should().BeLessThan(TimeSpan.FromHours(1));
    }

    [Fact]
    public void A_busy_half_hour_does_not_condemn_the_week()
    {
        // 20 %/h for half an hour: fast, and nowhere near enough to claim the week
        // will not last the four days it has left. This was the noisiest false
        // positive the endpoint-rate version produced - it fired on any sustained
        // working session and then renotified every fifteen minutes.
        VelocityAlert? alert = Run(
            sessionFrom: 2,
            sessionRate: 20,
            weekFrom: 30,
            weekRate: 20,
            minutes: 30,
            sessionResetsIn: TimeSpan.FromHours(5),
            weekResetsIn: TimeSpan.FromDays(4));

        alert.Should().BeNull();
    }

    [Fact]
    public void Nothing_is_said_while_a_limit_is_spent()
    {
        // The week is out. The user is cut off, so the session cannot be climbing
        // towards anything - and the indicators already show the exhausted mark.
        VelocityAlert? alert = Run(
            sessionFrom: 40,
            sessionRate: 60,
            weekFrom: 100,
            weekRate: 0,
            minutes: 10,
            sessionResetsIn: TimeSpan.FromHours(2),
            weekResetsIn: TimeSpan.FromDays(5));

        alert.Should().BeNull();
    }

    [Fact]
    public void An_exhausted_fable_window_silences_the_warning_too()
    {
        VelocityAlert? alert = Run(
            sessionFrom: 40,
            sessionRate: 60,
            weekFrom: 20,
            weekRate: 1,
            minutes: 10,
            sessionResetsIn: TimeSpan.FromHours(2),
            weekResetsIn: TimeSpan.FromDays(5),
            fable: 100);

        alert.Should().BeNull();
    }

    [Fact]
    public void Turning_the_warnings_off_silences_them()
    {
        Run(
            sessionFrom: 40,
            sessionRate: 60,
            weekFrom: 20,
            weekRate: 1,
            minutes: 10,
            sessionResetsIn: TimeSpan.FromHours(2),
            weekResetsIn: TimeSpan.FromDays(5),
            enabled: false)
            .Should().BeNull();
    }

    [Fact]
    public void Samples_are_kept_while_the_warnings_are_off_so_switching_them_on_is_immediate()
    {
        var tracker = new VelocityTracker();
        for (int m = 0; m <= 10; m++)
        {
            DateTimeOffset at = T0.AddMinutes(m);
            tracker.Observe(Snapshot(40 + (m * 1d), at), at, enabled: false);
        }

        tracker.SessionSamples.Should().HaveCount(11);
        tracker.Current.Should().BeNull();

        // The eleventh reading, with the setting on: the history is already there.
        DateTimeOffset now = T0.AddMinutes(11);
        tracker.Observe(Snapshot(51, now), now, enabled: true).Should().NotBeNull();
    }

    [Fact]
    public void A_stale_reading_changes_nothing()
    {
        var tracker = new VelocityTracker();
        for (int m = 0; m <= 10; m++)
        {
            DateTimeOffset at = T0.AddMinutes(m);
            tracker.Observe(Snapshot(40 + (m * 1d), at), at, enabled: true);
        }

        VelocityAlert? raised = tracker.Current;
        raised.Should().NotBeNull();

        // A cached number carries a timestamp from before the app started, and a
        // rate measured against it is about a different day. The pace has not been
        // disproved, so the warning stands.
        DateTimeOffset now = T0.AddMinutes(11);
        UsageSnapshot stale = Snapshot(51, now) with { IsStale = true };

        tracker.Observe(stale, now, enabled: true).Should().Be(raised);
        tracker.SessionSamples.Should().HaveCount(11, "a stale reading is not a sample");
    }

    [Fact]
    public void A_null_reading_changes_nothing()
    {
        var tracker = new VelocityTracker();

        tracker.Observe(null, T0, enabled: true).Should().BeNull();
        tracker.SessionSamples.Should().BeEmpty();
    }

    [Fact]
    public void Resetting_forgets_the_readings_and_the_warning()
    {
        var tracker = new VelocityTracker();
        for (int m = 0; m <= 10; m++)
        {
            DateTimeOffset at = T0.AddMinutes(m);
            tracker.Observe(Snapshot(40 + (m * 1d), at), at, enabled: true);
        }

        tracker.Current.Should().NotBeNull();

        tracker.Reset();

        tracker.Current.Should().BeNull();
        tracker.SessionSamples.Should().BeEmpty();
        tracker.WeekSamples.Should().BeEmpty();
    }

    /// <summary>A session climbing 60 %/h with two hours to its reset, and a quiet week.</summary>
    private static UsageSnapshot Snapshot(double sessionPercent, DateTimeOffset at)
        => new(
            new UsageWindow(sessionPercent, T0.AddHours(2)),
            new UsageWindow(20, T0.AddDays(5)),
            null,
            new Dictionary<string, UsageWindow>(),
            at,
            IsStale: false);
}
