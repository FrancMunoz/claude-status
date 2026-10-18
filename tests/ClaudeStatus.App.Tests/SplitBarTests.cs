namespace ClaudeStatus.App.Tests;

/// <summary>
/// The weekly bar cut by surface, in the popup, the hover card and the taskbar
/// card - the same split the iCUE widget draws.
/// </summary>
public class SplitBarTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static UsageBreakdown Split(params (string Key, double Percent)[] rows)
        => new(Now, null, [.. rows.Select(row => new UsageBreakdownRow(row.Key, null, row.Percent))]);

    private static UsageBarViewModel Bar(UsageBreakdown? split, UsageWindow? window = null)
    {
        var bar = new UsageBarViewModel(TestLocalizer.English(), "Details_Metric_Week");
        bar.Update(window ?? UsageWindow.Create(91, Now.AddDays(2)), ThresholdState.Exceeded, Now, split: split);
        return bar;
    }

    [Fact]
    public void Segments_are_ranked_biggest_first_and_the_key_follows_them()
    {
        UsageBarViewModel bar = Bar(Split(("claude_code", 20), ("chat", 3), ("cowork", 77), ("other", 0)));

        bar.HasSplit.Should().BeTrue();
        bar.Segments.Should().Equal(77, 20, 3);
        bar.SplitKeys.Select(key => key.Label).Should().Equal("Cowork", "Claude Code", "Chats");
        bar.SplitKeys.Select(key => key.Opacity).Should().Equal(Controls.UsageBar.SegmentShades.Take(3));
        bar.SplitKeys[0].PercentText.Should().Be("77 %");
    }

    [Fact]
    public void Shares_too_small_to_draw_and_shares_past_the_last_shade_are_left_out()
    {
        UsageBarViewModel bar = Bar(Split(("a", 40), ("b", 30), ("c", 15), ("d", 10), ("e", 4.6), ("f", 0.4)));

        bar.Segments.Should().Equal(40, 30, 15, 10);
    }

    [Fact]
    public void One_surface_is_no_split()
    {
        UsageBarViewModel bar = Bar(Split(("claude_code", 100), ("chat", 0)));

        bar.HasSplit.Should().BeFalse();
        bar.Segments.Should().BeNull();
        bar.SplitKeys.Should().BeEmpty();
    }

    [Fact]
    public void No_breakdown_or_no_reading_draws_the_plain_fill()
    {
        Bar(null).Segments.Should().BeNull();

        var bar = new UsageBarViewModel(TestLocalizer.English(), "Details_Metric_Week");
        bar.Update(null, ThresholdState.Unknown, Now, split: Split(("a", 50), ("b", 50)));
        bar.HasSplit.Should().BeFalse();
    }

    [Fact]
    public void The_key_is_rebuilt_in_the_new_language()
    {
        var localizer = TestLocalizer.English();
        var bar = new UsageBarViewModel(localizer, "Details_Metric_Week");
        bar.Update(UsageWindow.Create(50, null), ThresholdState.Normal, Now, split: Split(("chat", 60), ("other", 40)));

        localizer.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("es"));

        bar.SplitKeys.Select(key => key.Label).Should().Equal("Chats", "Otros");
    }

    [Fact]
    public void The_popup_splits_only_the_weekly_bar()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now);
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);
        using var details = new DetailsViewModel(monitor, () => new AppSettings(), TestLocalizer.English(), clock);

        details.Apply(new UsageSnapshot(
            UsageWindow.Create(35, Now.AddHours(2)),
            UsageWindow.Create(91, Now.AddDays(2)),
            UsageWindow.Create(54, Now.AddDays(2)),
            new Dictionary<string, UsageWindow>(),
            Now,
            false)
        { WeekBreakdown = Split(("cowork", 77), ("claude_code", 20), ("chat", 3)) });

        details.Week.HasSplit.Should().BeTrue();
        details.Session.HasSplit.Should().BeFalse();
        details.WeekFable.HasSplit.Should().BeFalse();
    }
}
