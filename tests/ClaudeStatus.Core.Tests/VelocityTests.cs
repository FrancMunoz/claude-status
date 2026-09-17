using ClaudeStatus.Usage;

namespace ClaudeStatus.Core.Tests;

/// <summary>Whether usage is climbing fast enough to run out before the reset.</summary>
public class VelocityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static UsageSample At(int minutes, double percent) => new(T0.AddMinutes(minutes), percent);

    /// <summary>A steady climb, one sample a minute, the way the poll loop produces them.</summary>
    private static UsageSample[] Climb(double from, double percentPerHour, int minutes)
        => [.. Enumerable.Range(0, minutes + 1).Select(m => At(m, from + (percentPerHour * m / 60d)))];

    [Fact]
    public void A_pace_that_exhausts_the_window_before_reset_raises_an_alert()
    {
        // 60 %/h from 40 % leaves 40 % to spend, so 40 minutes to the wall - and
        // the window does not reset for two hours.
        UsageSample[] samples = Climb(from: 40, percentPerHour: 60, minutes: 10);

        VelocityAlert? alert = VelocityRule.Evaluate(
            VelocityWindow.Session, samples, T0.AddHours(2), T0.AddMinutes(10));

        alert.Should().NotBeNull();
        alert!.Window.Should().Be(VelocityWindow.Session);
        alert.PercentPerHour.Should().BeApproximately(60d, 0.01d);
        alert.UntilExhausted.Should().BeCloseTo(TimeSpan.FromMinutes(50), TimeSpan.FromSeconds(1));
        alert.UntilReset.Should().Be(TimeSpan.FromMinutes(110));
    }

    [Fact]
    public void A_pace_the_reset_will_beat_is_not_an_alert()
    {
        // 10 %/h with 50 % left needs five hours; the window resets in one.
        UsageSample[] samples = Climb(from: 48, percentPerHour: 10, minutes: 12);

        VelocityRule.Evaluate(VelocityWindow.Session, samples, T0.AddHours(1), T0.AddMinutes(12))
            .Should().BeNull("the reset arrives long before the wall does");
    }

    [Fact]
    public void Samples_too_close_together_do_not_make_a_rate()
    {
        // One long request landing between two polls looks like a cliff. Two
        // minutes is not enough to tell that from a trend.
        UsageSample[] samples = [At(0, 10), At(1, 17), At(2, 24), At(2, 30)];

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
    public void Two_samples_are_not_enough_however_far_apart_they_are()
    {
        // Two points fit a straight line perfectly, so the fit gate is blind to
        // them: a pair an hour apart cannot say whether anything happened between.
        VelocityRule.Rate([At(0, 10), At(60, 80)]).Should().BeNull();
    }

    [Fact]
    public void One_burst_at_the_end_of_a_flat_stretch_is_not_a_pace()
    {
        // The endpoints are identical to a steady climb from 50 to 70, and the
        // endpoint rate this replaced could not tell the two apart. Nothing moved
        // for twenty minutes and then one request landed; that is a burst, and a
        // burst is not a rate to project.
        UsageSample[] samples =
        [
            At(0, 50), At(5, 50), At(10, 50), At(15, 50), At(20, 50), At(25, 70),
        ];

        UsageTrend trend = VelocityRule.Measure(samples)!.Value;
        trend.Fit.Should().BeLessThan(VelocityRule.MinimumFit, "a flat line with a step is a poor fit");

        VelocityRule.Evaluate(VelocityWindow.Session, samples, T0.AddHours(2), T0.AddMinutes(25))
            .Should().BeNull();
    }

    [Fact]
    public void A_steady_climb_is_a_good_fit()
    {
        UsageTrend trend = VelocityRule.Measure(Climb(from: 10, percentPerHour: 30, minutes: 20))!.Value;

        trend.PercentPerHour.Should().BeApproximately(30d, 0.01d);
        trend.Fit.Should().BeApproximately(1d, 0.0001d);
        trend.Span.Should().Be(TimeSpan.FromMinutes(20));
        trend.Count.Should().Be(21);
    }

    [Fact]
    public void A_wall_further_off_than_the_measurement_can_reach_is_not_an_alert()
    {
        // Ten minutes of samples, and the wall is nine hours away. True at this
        // instant, and not something ten minutes of readings can claim: nobody
        // holds a pace for nine hours, and the reset is five days out.
        UsageSample[] samples = Climb(from: 45, percentPerHour: 6, minutes: 10);

        VelocityRule.Evaluate(VelocityWindow.Week, samples, T0.AddDays(5), T0.AddMinutes(10))
            .Should().BeNull("ten minutes cannot be projected nine hours");
    }

    [Fact]
    public void A_wall_within_reach_of_the_measurement_is_an_alert()
    {
        // Three hours of samples - the week's horizon - projecting six hours out.
        // Same arithmetic as above, with a measurement long enough to carry it.
        UsageSample[] samples = Climb(from: 45, percentPerHour: 6, minutes: 180);

        VelocityRule.Evaluate(VelocityWindow.Week, samples, T0.AddDays(5), T0.AddMinutes(180))
            .Should().NotBeNull();
    }

    [Fact]
    public void A_week_still_inside_its_budget_is_not_an_alert()
    {
        // Six days gone and 78 % spent: the pace reaches the wall well before the
        // reset, but 86 % of the week has passed to spend it in. A busy last
        // afternoon of a frugal week is not a week about to run out.
        UsageSample[] samples = Climb(from: 60, percentPerHour: 6, minutes: 180);

        VelocityRule.Evaluate(
                VelocityWindow.Week,
                samples,
                T0.AddDays(1),
                T0.AddMinutes(180),
                UsageWindowSpans.Week)
            .Should().BeNull("more of the window has gone than of the allowance");

        // And the budget is the only thing holding it back, which is what makes
        // this a test of the budget rather than of the gates around it.
        VelocityRule.Evaluate(VelocityWindow.Week, samples, T0.AddDays(1), T0.AddMinutes(180))
            .Should().NotBeNull("without the budget check the projection alone says yes");
    }

    [Fact]
    public void A_week_ahead_of_its_budget_is_an_alert()
    {
        // Two days in and 88 % spent, still climbing: 30 % of the week gone and
        // 88 % of the allowance with it.
        UsageSample[] samples = Climb(from: 58, percentPerHour: 10, minutes: 180);

        VelocityRule.Evaluate(
                VelocityWindow.Week,
                samples,
                T0.AddDays(5),
                T0.AddMinutes(180),
                UsageWindowSpans.Week)
            .Should().NotBeNull();
    }

    [Fact]
    public void A_crawl_never_alerts_even_when_it_would_technically_exhaust_first()
    {
        // 99 % and rising 1 %/h "runs out" in an hour, ahead of a two-hour reset.
        // That is arithmetic, not news; the icon is already red.
        UsageSample[] samples = Climb(from: 98.9, percentPerHour: 1, minutes: 6);

        VelocityRule.Evaluate(VelocityWindow.Session, samples, T0.AddHours(2), T0.AddMinutes(6))
            .Should().BeNull();
    }

    [Fact]
    public void A_borderline_pace_holds_the_alert_it_already_raised()
    {
        // 4 %/h is under the 5 %/h floor, so it cannot raise an alert - but it must
        // not cancel one either, or a pace wandering either side of the line makes
        // the warning blink once a minute.
        UsageSample[] samples = Climb(from: 90, percentPerHour: 4, minutes: 30);

        VelocityRule.Evaluate(VelocityWindow.Session, samples, T0.AddHours(3.5), T0.AddMinutes(30))
            .Should().BeNull("below the floor, nothing to raise");

        VelocityRule.Evaluate(
                VelocityWindow.Session, samples, T0.AddHours(3.5), T0.AddMinutes(30), latched: true)
            .Should().NotBeNull("already warned; the pace has not actually recovered");
    }

    [Fact]
    public void An_exhausted_window_is_not_a_velocity_problem()
    {
        UsageSample[] samples = Climb(from: 80, percentPerHour: 120, minutes: 10);

        VelocityRule.Evaluate(VelocityWindow.Session, samples, T0.AddHours(2), T0.AddMinutes(10))
            .Should().BeNull("the cliff has been gone over; the exhausted state says so");
    }

    [Fact]
    public void A_window_the_indicators_already_call_spent_is_not_a_velocity_problem()
    {
        // 99.96 % displays as 100 and draws the exhausted mark, so a warning
        // about running out would be contradicting the number beside it.
        UsageSample[] samples = [At(0, 80), At(4, 86.65), At(8, 93.3), At(12, 99.96)];

        VelocityRule.Evaluate(VelocityWindow.Session, samples, T0.AddHours(2), T0.AddMinutes(12))
            .Should().BeNull("the indicator already shows 100 %");
    }

    [Fact]
    public void An_unknown_reset_time_means_no_alert()
    {
        UsageSample[] samples = Climb(from: 40, percentPerHour: 120, minutes: 10);

        VelocityRule.Evaluate(VelocityWindow.Week, samples, null, T0.AddMinutes(10)).Should().BeNull();
    }

    [Fact]
    public void Falling_usage_is_never_an_alert()
    {
        UsageSample[] samples = Climb(from: 60, percentPerHour: -40, minutes: 10);

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
    public void A_longer_horizon_keeps_the_older_samples()
    {
        var history = new UsageHistory(TimeSpan.FromHours(3));
        history.Add(At(0, 10));
        history.Add(At(20, 20));
        history.Add(At(45, 30));

        history.Samples.Should().HaveCount(3);
    }

    [Fact]
    public void A_horizon_shorter_than_the_minimum_span_is_raised_to_it()
    {
        // A history that cannot hold enough time to measure a rate would be a
        // history nothing can ever be measured from.
        new UsageHistory(TimeSpan.FromSeconds(30)).Horizon.Should().Be(VelocityRule.MinimumSpan);
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
        foreach (UsageSample sample in Climb(from: 30, percentPerHour: 180, minutes: 10))
        {
            history.Add(sample);
        }

        VelocityRule.Evaluate(VelocityWindow.Session, history.Samples, T0.AddHours(3), T0.AddMinutes(10))
            .Should().NotBeNull();
    }
}
