namespace ClaudeStatus.Core.Tests;

public class ThresholdEvaluatorTests
{
    [Theory]
    [InlineData(79.9, 80, ThresholdState.Normal)]
    [InlineData(80.0, 80, ThresholdState.Exceeded)]
    [InlineData(80.1, 80, ThresholdState.Exceeded)]
    [InlineData(0, 80, ThresholdState.Normal)]
    [InlineData(100, 100, ThresholdState.Exceeded)]
    public void Crosses_at_the_threshold_inclusive(double percent, double threshold, ThresholdState expected)
    {
        ThresholdEvaluator.Evaluate(UsageWindow.Create(percent, null), threshold).Should().Be(expected);
    }

    [Fact]
    public void A_missing_window_is_Unknown_not_Normal()
    {
        // "We do not know" must not render as calm - that would quietly show a
        // green icon while the user is at 100 %.
        ThresholdEvaluator.Evaluate((UsageWindow?)null, 80d).Should().Be(ThresholdState.Unknown);
    }

    [Fact]
    public void The_row_takes_the_worst_verdict_of_the_windows_it_shows()
    {
        // 29 / 58 / 88. The row draws session and week side by side, so a calm
        // session must not make a week past the threshold look calm too.
        UsageSnapshot snapshot = Fixture.Parse(Fixture.Normal);

        ThresholdEvaluator.EvaluateRow(snapshot, 50d, includeWeekFable: false)
            .Should().Be(ThresholdState.Exceeded, "the weekly window is at 58 %");

        ThresholdEvaluator.EvaluateRow(snapshot, 80d, includeWeekFable: false)
            .Should().Be(ThresholdState.Normal, "neither shown window has reached 80 %");
    }

    [Fact]
    public void The_row_ignores_a_Fable_window_it_is_not_drawing()
    {
        // 88 % Fable, and the user turned that column off. Turning the row red for
        // a reading that is not on screen is a warning about nothing visible.
        UsageSnapshot snapshot = Fixture.Parse(Fixture.Normal);

        ThresholdEvaluator.EvaluateRow(snapshot, 80d, includeWeekFable: false)
            .Should().Be(ThresholdState.Normal);

        ThresholdEvaluator.EvaluateRow(snapshot, 80d, includeWeekFable: true)
            .Should().Be(ThresholdState.Exceeded);
    }

    [Fact]
    public void A_row_with_a_missing_window_is_Unknown_rather_than_Normal()
    {
        // Same promise the single-window evaluator makes: a window we know nothing
        // about must never leave the indicator looking calm.
        var snapshot = new UsageSnapshot(
            UsageWindow.Create(10d, null),
            null,
            null,
            new Dictionary<string, UsageWindow>(),
            DateTimeOffset.UnixEpoch,
            false);

        ThresholdEvaluator.EvaluateRow(snapshot, 80d, includeWeekFable: false)
            .Should().Be(ThresholdState.Unknown);
    }

    [Fact]
    public void A_null_snapshot_has_no_row_verdict_either()
    {
        ThresholdEvaluator.EvaluateRow(null, 80d, includeWeekFable: true)
            .Should().Be(ThresholdState.Unknown);
    }

    [Fact]
    public void A_null_snapshot_is_Unknown()
    {
        ThresholdEvaluator.Evaluate(null, IndicatorMode.SessionPercent, 80d)
            .Should().Be(ThresholdState.Unknown);
    }

    [Fact]
    public void Evaluates_the_window_the_current_mode_displays()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.Normal); // 29 / 58 / 88

        ThresholdEvaluator.Evaluate(snapshot, IndicatorMode.SessionPercent, 80d).Should().Be(ThresholdState.Normal);
        ThresholdEvaluator.Evaluate(snapshot, IndicatorMode.WeekPercent, 80d).Should().Be(ThresholdState.Normal);
        ThresholdEvaluator.Evaluate(snapshot, IndicatorMode.WeekFablePercent, 80d).Should().Be(ThresholdState.Exceeded);
    }

    [Fact]
    public void Ring_mode_tracks_the_session_window()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.Normal);

        snapshot.ForMode(IndicatorMode.Ring).Should().BeSameAs(snapshot.Session);
    }

    [Fact]
    public void EvaluateAll_reports_each_window_independently()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.Normal);

        (ThresholdState session, ThresholdState week, ThresholdState fable) =
            ThresholdEvaluator.EvaluateAll(snapshot, 80d);

        session.Should().Be(ThresholdState.Normal);
        week.Should().Be(ThresholdState.Normal);
        fable.Should().Be(ThresholdState.Exceeded);
    }

    [Fact]
    public void EvaluateAll_marks_a_missing_Fable_window_Unknown()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.NoScopedWindow);

        ThresholdEvaluator.EvaluateAll(snapshot, 80d).WeekFable.Should().Be(ThresholdState.Unknown);
    }

    [Theory]
    [InlineData(0d, ThresholdEvaluator.MinThresholdPercent)]
    [InlineData(-10d, ThresholdEvaluator.MinThresholdPercent)]
    [InlineData(500d, ThresholdEvaluator.MaxThresholdPercent)]
    [InlineData(80d, 80d)]
    public void Clamp_forces_a_configured_threshold_into_range(double raw, double expected)
    {
        ThresholdEvaluator.Clamp(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Clamp_falls_back_to_the_default_for_a_non_finite_threshold(double raw)
    {
        ThresholdEvaluator.Clamp(raw).Should().Be(ThresholdEvaluator.DefaultThresholdPercent);
    }
}
