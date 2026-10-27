using ClaudeStatus.App.Tray;
using ClaudeStatus.Localization;
using ClaudeStatus.Platform;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// The credits row as the popup, the hover card, the widget and the tray
/// tooltip word it.
/// </summary>
public class CreditsTests
{
    private static readonly Localizer L = TestLocalizer.English();
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static UsageSnapshot Snapshot(double week = 100d, bool enabled = true, bool capReached = false) => new(
        UsageWindow.Create(11d, Now.AddHours(2)),
        UsageWindow.Create(week, Now.AddDays(2)),
        null,
        new Dictionary<string, UsageWindow>(),
        Now,
        IsStale: false)
    {
        Spend = new SpendInfo(
            enabled, new MoneyAmount(562, "EUR", 2), new MoneyAmount(10000, "EUR", 2), 6d, "normal", null),
        ExtraUsage = new ExtraUsageInfo(enabled, false, capReached, true, 10000d, 562d, 5.62d, "EUR", null)
        {
            DecimalPlaces = 2,
        },
    };

    [Fact]
    public void On_credits_the_row_says_so_with_what_is_spent_and_left()
    {
        var model = new CreditsViewModel(L);
        model.Update(Snapshot());

        model.IsShown.Should().BeTrue();
        model.IsPastLimits.Should().BeTrue();
        model.IsExceeded.Should().BeFalse("being charged is not the cap running out");
        model.StateText.Should().Be("On credits");
        model.AmountText.Should().Be("5.62 EUR");
        model.DetailText.Should().Be("5.62 EUR of 100.00 EUR · 94.38 EUR left this month");
        model.Percent.Should().BeApproximately(5.62, 1e-9);
    }

    [Fact]
    public void The_widget_sets_the_currency_after_the_amount()
    {
        var model = new CreditsViewModel(L);
        model.Update(Snapshot());

        model.ShortLabel.Should().Be("Credits");
        model.AmountNumberText.Should().Be("5.62");
        model.CurrencySign.Should().Be("€");
    }

    [Fact]
    public void On_standby_nothing_mentions_credits()
    {
        var model = new CreditsViewModel(L);
        model.Update(Snapshot(week: 40d));

        model.IsShown.Should().BeFalse();
        model.IsPastLimits.Should().BeFalse();
        TrayIconIndicator.BuildTooltip(L, Snapshot(week: 40d), IndicatorMode.WeekPercent)
            .Should().NotContain("credits");
    }

    [Fact]
    public void On_credits_the_widget_folds_the_spent_week_into_the_credits_column()
    {
        UsageSnapshot reading = Snapshot() with { WeekFable = UsageWindow.Create(100d, Now.AddDays(2)) };
        var widget = new TaskbarWidgetViewModel(L);
        widget.Configure(80d, showFable: true);

        widget.Update(reading, IndicatorAlert.None, Now);

        widget.ShowSessionColumn.Should().BeTrue();
        widget.ShowWeekColumn.Should().BeFalse("its 100 % is what the credits column now says");
        widget.ShowFableColumn.Should().BeFalse();
        widget.CreditsTimePercent.Should().Be(widget.Week.TimePercent);
    }

    [Fact]
    public void Off_credits_or_with_the_cap_spent_every_column_stays()
    {
        var widget = new TaskbarWidgetViewModel(L);

        widget.Update(Snapshot(enabled: false), IndicatorAlert.None, Now);
        widget.ShowWeekColumn.Should().BeTrue("spent with nothing paying is a block, and must show");

        widget.Update(Snapshot(capReached: true), IndicatorAlert.None, Now);
        widget.ShowWeekColumn.Should().BeTrue();
    }

    [Fact]
    public void A_spent_cap_takes_the_alert()
    {
        var model = new CreditsViewModel(L);
        model.Update(Snapshot(capReached: true));

        model.IsExceeded.Should().BeTrue();
        model.StateText.Should().Be("Monthly cap reached");
    }

    [Fact]
    public void Credits_switched_off_hide_the_row()
    {
        var model = new CreditsViewModel(L);
        model.Update(Snapshot(enabled: false));

        model.IsShown.Should().BeFalse();
        model.IsPastLimits.Should().BeFalse();
    }

    [Fact]
    public void The_tray_tooltip_adds_a_credits_line()
        => TrayIconIndicator.BuildTooltip(L, Snapshot(), IndicatorMode.WeekPercent)
            .Should().EndWith(Environment.NewLine + "On credits: 5.62 EUR of 100.00 EUR · 94.38 EUR left this month");

    [Fact]
    public void The_widget_carries_the_credits_and_drops_them_with_the_reading()
    {
        var widget = new TaskbarWidgetViewModel(L);

        widget.Update(Snapshot(), IndicatorAlert.None, Now);
        widget.Credits.IsPastLimits.Should().BeTrue();

        widget.Update(null, IndicatorAlert.NeedsCredential, Now);
        widget.Credits.IsShown.Should().BeFalse();
    }
}
