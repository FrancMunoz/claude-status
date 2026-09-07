using ClaudeStatus.App.Tray;
using ClaudeStatus.Localization;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// A localizer over the real compiled resources, in English.
/// </summary>
/// <remarks>
/// Deliberately the real thing rather than a stub returning keys. These tests
/// assert on the sentences a user reads, and a stub would let a missing or
/// renamed key pass unnoticed - which is exactly the failure mode a resource
/// system introduces.
/// </remarks>
internal static class TestLocalizer
{
    public static Localizer English()
    {
        var localizer = new Localizer();
        localizer.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("en"));
        return localizer;
    }
}

public class UsageBarFormattingTests
{
    private static readonly Localizer L = TestLocalizer.English();

    [Fact]
    public void An_unknown_window_shows_a_dash_not_a_zero()
    {
        // Zero and "we do not know" mean very different things to someone
        // deciding whether to start a long task.
        var bar = new UsageBarViewModel(L, "Details_Metric_Session");

        bar.Update(null, ThresholdState.Unknown, DateTimeOffset.UnixEpoch);

        bar.PercentText.Should().Be("—");
        bar.IsKnown.Should().BeFalse();
        bar.Percent.Should().Be(0);
    }

    [Fact]
    public void A_known_window_shows_its_percentage_and_countdown()
    {
        var bar = new UsageBarViewModel(L, "Details_Metric_Session");
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        bar.Update(UsageWindow.Create(29d, now.AddHours(2).AddMinutes(37)), ThresholdState.Normal, now);

        bar.PercentText.Should().Be("29 %");
        bar.ResetText.Should().Be("resets in 2h 37m");
        bar.IsKnown.Should().BeTrue();
        bar.IsExceeded.Should().BeFalse();
    }

    [Fact]
    public void The_label_comes_from_the_resources_not_the_key()
    {
        new UsageBarViewModel(L, "Details_Metric_WeekFable").Label.Should().Be("Week (Fable)");
    }

    [Fact]
    public void An_exceeded_window_is_flagged_for_the_red_style()
    {
        var bar = new UsageBarViewModel(L, "Details_Metric_Week");

        bar.Update(UsageWindow.Create(88d, null), ThresholdState.Exceeded, DateTimeOffset.UnixEpoch);

        bar.IsExceeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "reset time unknown")]
    [InlineData(0, "resetting now")]
    [InlineData(30, "resets in under a minute")]
    [InlineData(90, "resets in 1m")]
    [InlineData(3600 + 1500, "resets in 1h 25m")]
    [InlineData(86400 * 2 + 3600 * 6, "resets in 2d 6h")]
    [InlineData(86400 * 3, "resets in 3d")]
    public void Countdowns_read_the_way_a_person_would_say_them(int? seconds, string expected)
    {
        TimeSpan? span = seconds is null ? null : TimeSpan.FromSeconds(seconds.Value);

        UsageBarViewModel.FormatReset(L, span).Should().Be(expected);
    }

    [Fact]
    public void A_countdown_that_has_already_passed_never_shows_a_negative()
    {
        UsageBarViewModel.FormatReset(L, TimeSpan.FromMinutes(-5)).Should().Be("resetting now");
    }
}

public class DetailsFormattingTests
{
    private static readonly Localizer L = TestLocalizer.English();

    [Theory]
    [InlineData(30, "moments")]
    [InlineData(90, "1 min")]
    [InlineData(3600 * 2 + 60 * 5, "2h 5m")]
    [InlineData(86400 * 2, "2d")]
    public void Ages_are_described_in_the_coarsest_sensible_unit(int seconds, string expected)
    {
        DetailsViewModel.DescribeAge(L, TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void A_missing_credential_is_explained_rather_than_shown_as_a_generic_error()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);

        using var viewModel = new DetailsViewModel(monitor, () => new AppSettings(), L, clock);

        viewModel.StatusText.Should().Contain("Waiting");
    }

    [Fact]
    public void Having_no_reading_yet_does_not_raise_the_stale_banner()
    {
        // UsageSnapshot.IsStale is true before the first poll returns, so passing
        // it straight through drew an empty warning box on every cold start.
        // Staleness is about a reading that has gone old; there isn't one yet.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);

        using var viewModel = new DetailsViewModel(monitor, () => new AppSettings(), L, clock);

        viewModel.HasStaleReading.Should().BeFalse();
        viewModel.StaleText.Should().BeEmpty();
    }

    [Fact]
    public void An_old_reading_does_raise_it_and_says_how_old()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var clock = new FakeTimeProvider(now);
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);

        using var viewModel = new DetailsViewModel(monitor, () => new AppSettings(), L, clock);
        viewModel.Apply(new UsageSnapshot(
            Session: UsageWindow.Create(29d, now.AddHours(1)),
            Week: UsageWindow.Create(58d, now.AddDays(2)),
            WeekFable: null,
            OtherWindows: new Dictionary<string, UsageWindow>(),
            FetchedAt: now.AddHours(-3),
            IsStale: true));

        viewModel.HasStaleReading.Should().BeTrue();
        viewModel.StaleText.Should().NotBeEmpty();
    }
}

public class TrayTooltipTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly Localizer L = TestLocalizer.English();

    private static UsageSnapshot Snapshot(bool stale = false, bool withFable = true) => new(
        Session: UsageWindow.Create(29d, Now),
        Week: UsageWindow.Create(58d, Now),
        WeekFable: withFable ? UsageWindow.Create(88d, Now) : null,
        OtherWindows: new Dictionary<string, UsageWindow>(),
        FetchedAt: Now,
        IsStale: stale);

    [Fact]
    public void The_tooltip_lists_all_three_metrics()
    {
        // It is the only place all three appear without opening a window.
        string tooltip = TrayIconIndicator.BuildTooltip(L, Snapshot(), IndicatorMode.SessionPercent);

        tooltip.Should().Contain("29").And.Contain("58").And.Contain("88");
    }

    [Fact]
    public void The_tooltip_says_when_the_data_is_stale()
    {
        TrayIconIndicator.BuildTooltip(L, Snapshot(stale: true), IndicatorMode.SessionPercent)
            .Should().Contain("stale");
    }

    [Fact]
    public void The_tooltip_reports_a_missing_Fable_window_as_not_applicable()
    {
        TrayIconIndicator.BuildTooltip(L, Snapshot(withFable: false), IndicatorMode.SessionPercent)
            .Should().Contain("n/a");
    }

    [Fact]
    public void The_tooltip_is_honest_before_the_first_reading()
    {
        TrayIconIndicator.BuildTooltip(L, null, IndicatorMode.SessionPercent)
            .Should().Contain("no data");
    }

    [Theory]
    [InlineData(0d, "0")]
    [InlineData(29.4d, "29")]
    [InlineData(99.6d, "100")]
    [InlineData(100d, "100")]
    [InlineData(150d, "100")]
    public void Percentages_round_away_from_zero_and_clamp(double percent, string expected)
    {
        // 99.6 must not show as 99: that would suggest headroom that is not there.
        TrayIconRenderer.FormatPercent(percent).Should().Be(expected);
    }
}

public class InfoContentTests
{
    [Fact]
    public void The_unofficial_notice_says_the_endpoint_is_undocumented_and_unaffiliated()
    {
        // Required by docs/manual.md §3. Someone comparing our numbers with claude.ai
        // deserves to know what they are reading.
        string notice = TestLocalizer.English()["Info_UnofficialNotice"];

        notice.Should().Contain("undocumented");
        notice.Should().Contain("not affiliated");
    }

    [Fact]
    public void A_version_is_always_reported()
    {
        InfoViewModel.Version.Should().NotBeNullOrWhiteSpace();
    }
}
