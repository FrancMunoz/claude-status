using ClaudeStatus.Usage;

namespace ClaudeStatus.Core.Tests;

/// <summary>Whether usage is climbing fast enough to run out before the reset.</summary>
public class VelocityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static UsageSample At(int minutes, double percent) => new(T0.AddMinutes(minutes), percent);

    [Fact]
    public void A_pace_that_exhausts_the_window_before_reset_raises_an_alert()
    {
        // 40 % to 60 % in 10 minutes is 120 %/h. With 40 % left that is 20 minutes
        // to the wall, and the window does not reset for two hours.
        UsageSample[] samples = [At(0, 40), At(5, 50), At(10, 60)];

        VelocityAlert? alert = VelocityRule.Evaluate(
            VelocityWindow.Session, samples, T0.AddHours(2), T0.AddMinutes(10));

        alert.Should().NotBeNull();
        alert!.Window.Should().Be(VelocityWindow.Session);
        alert.PercentPerHour.Should().BeApproximately(120d, 0.01d);
        alert.UntilExhausted.Should().BeCloseTo(TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(1));
        alert.UntilReset.Should().Be(TimeSpan.FromMinutes(110));
    }

    [Fact]
    public void A_pace_the_reset_will_beat_is_not_an_alert()
    {
        // 10 %/h with 50 % left needs five hours; the window resets in one.
        UsageSample[] samples = [At(0, 48), At(6, 49), At(12, 50)];

        VelocityRule.Evaluate(VelocityWindow.Session, samples, T0.AddHours(1), T0.AddMinutes(12))
            .Should().BeNull("the reset arrives long before the wall does");
    }

    [Fact]
    public void Samples_too_close_together_do_not_make_a_rate()
    {
        // One long request landing between two polls looks like a cliff. Two
        // minutes is not enough to tell that from a trend.
        UsageSample[] samples = [At(0, 10), At(2, 30)];

        VelocityRule.Rate(samples).Should().BeNull();
        VelocityRule.Evaluate(VelocityWindow.Session, samples, T0.AddHours(5), T0.AddMinutes(2))
            .Should().BeNull();
    }

    [Fact]
    public void A_single_sample_has_no_rate()
    {
        VelocityRule.Rate([At(0, 10)]).Should().BeNull();
    }

    [Fact]
    public void A_crawl_never_alerts_even_when_it_would_technically_exhaust_first()
    {
        // 99 % and rising 1 %/h "runs out" in an hour, ahead of a two-hour reset.
        // That is arithmetic, not news; the icon is already red.
        UsageSample[] samples = [At(0, 98.9), At(6, 99)];

        VelocityRule.Evaluate(VelocityWindow.Session, samples, T0.AddHours(2), T0.AddMinutes(6))
            .Should().BeNull();
    }

    [Fact]
    public void An_exhausted_window_is_not_a_velocity_problem()
    {
        UsageSample[] samples = [At(0, 80), At(10, 100)];

        VelocityRule.Evaluate(VelocityWindow.Session, samples, T0.AddHours(2), T0.AddMinutes(10))
            .Should().BeNull("the cliff has been gone over; the exhausted state says so");
    }

    [Fact]
    public void An_unknown_reset_time_means_no_alert()
    {
        UsageSample[] samples = [At(0, 40), At(10, 60)];

        VelocityRule.Evaluate(VelocityWindow.Week, samples, null, T0.AddMinutes(10)).Should().BeNull();
    }

    [Fact]
    public void Falling_usage_is_never_an_alert()
    {
        UsageSample[] samples = [At(0, 60), At(10, 40)];

        VelocityRule.Evaluate(VelocityWindow.Session, samples, T0.AddHours(2), T0.AddMinutes(10))
            .Should().BeNull();
    }

    [Fact]
    public void The_history_forgets_samples_older_than_the_horizon()
    {
        var history = new UsageHistory();
        history.Add(At(0, 10));
        history.Add(At(20, 20));
        history.Add(At(45, 30));

        history.Samples.Should().HaveCount(2, "the first sample is 45 minutes old");
        history.Samples[0].Percent.Should().Be(20);
    }

    [Fact]
    public void A_reset_wipes_the_history_so_the_old_window_cannot_pollute_the_rate()
    {
        var history = new UsageHistory();
        history.Add(At(0, 80));
        history.Add(At(5, 90));
        history.Add(At(10, 3));

        history.Samples.Should().ContainSingle().Which.Percent.Should().Be(3);
    }

    [Fact]
    public void The_history_feeds_the_rule_end_to_end()
    {
        var history = new UsageHistory();
        history.Add(At(0, 30));
        history.Add(At(5, 45));
        history.Add(At(10, 60));

        VelocityRule.Evaluate(VelocityWindow.Session, history.Samples, T0.AddHours(3), T0.AddMinutes(10))
            .Should().NotBeNull();
    }
}
