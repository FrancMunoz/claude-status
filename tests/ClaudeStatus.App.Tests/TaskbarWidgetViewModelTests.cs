using ClaudeStatus.App.ViewModels;
using ClaudeStatus.Localization;
using ClaudeStatus.Platform;
using ClaudeStatus.Usage;

namespace ClaudeStatus.App.Tests;

/// <summary>What the taskbar widget shows, and when it shows nothing.</summary>
public class TaskbarWidgetViewModelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Long enough after <see cref="Now"/> that a reading counts as out of date.
    /// </summary>
    /// <remarks>
    /// The widget follows <see cref="StalePolicy"/>: the monitor's flag says a
    /// refresh failed, and only age says the number has stopped being worth
    /// trusting. Both are needed, so a test about looking stale has to age it.
    /// </remarks>
    private static readonly DateTimeOffset MuchLater = Now + TimeSpan.FromMinutes(10);

    private static UsageSnapshot Snapshot(double session, double week, double fable, bool stale = false) => new(
        UsageWindow.Create(session, Now),
        UsageWindow.Create(week, Now),
        UsageWindow.Create(fable, Now),
        new Dictionary<string, UsageWindow>(),
        Now,
        stale);

    [Fact]
    public void Fable_is_hidden_until_the_setting_turns_it_on()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.ShowFable.Should().BeFalse("the default matches AppSettings.ShowFableInWidget");

        vm.Configure(80d, showFable: true);
        vm.ShowFable.Should().BeTrue();

        vm.Configure(80d, showFable: false);
        vm.ShowFable.Should().BeFalse();
    }

    [Fact]
    public void Blending_is_the_default_and_the_themed_card_is_opt_in()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.FollowsSystem.Should().BeTrue("the default matches AppSettings.WidgetFollowsSystem");

        vm.Configure(80d, showFable: false, followSystem: false);
        vm.FollowsSystem.Should().BeFalse();
    }

    [Fact]
    public void Blended_ink_follows_the_taskbar_and_defaults_to_the_dark_taskbar_white()
    {
        Tray.TaskbarInk.InkFor(TrayBackground.Dark).Should().Be(Tray.TaskbarInk.InkFor(TrayBackground.Unknown));
        Tray.TaskbarInk.InkFor(TrayBackground.Light).Should().NotBe(Tray.TaskbarInk.InkFor(TrayBackground.Dark));
    }

    [Fact]
    public void Every_metric_is_judged_against_the_threshold_not_just_the_one_on_the_icon()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());
        vm.Configure(80d, showFable: true);

        vm.Update(Snapshot(10, 90, 50), IndicatorAlert.None, Now);

        vm.HasReading.Should().BeTrue();
        vm.Session.IsExceeded.Should().BeFalse();
        vm.Week.IsExceeded.Should().BeTrue();
        vm.WeekFable.IsExceeded.Should().BeFalse();
    }

    [Fact]
    public void Changing_the_threshold_re_evaluates_the_current_reading()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());
        vm.Update(Snapshot(50, 10, 10), IndicatorAlert.None, Now);
        vm.Session.IsExceeded.Should().BeFalse();

        vm.Configure(40d, showFable: false);

        vm.Session.IsExceeded.Should().BeTrue("the reading did not change, the threshold did");
    }

    [Fact]
    public void A_stale_reading_is_flagged_and_still_shown()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.Update(Snapshot(61, 43, 88, stale: true), IndicatorAlert.None, MuchLater);

        vm.HasReading.Should().BeTrue();
        vm.IsStale.Should().BeTrue();
    }

    [Fact]
    public void One_failed_refresh_does_not_make_a_fresh_reading_look_doubtful()
    {
        // The endpoint 429s readily and the monitor flags a reading stale on the
        // first failure. Fading a number that arrived seconds ago on that basis
        // told the user to distrust the best information they had.
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.Update(Snapshot(61, 43, 88, stale: true), IndicatorAlert.None, Now);

        vm.HasReading.Should().BeTrue();
        vm.IsStale.Should().BeFalse();
    }

    [Fact]
    public void A_missing_credential_replaces_the_metrics_with_the_warning()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.Update(Snapshot(61, 43, 88), IndicatorAlert.NeedsCredential, Now);

        vm.HasReading.Should().BeFalse("nothing useful can be shown until the user acts");
        vm.AlertGlyph.Should().Be("!");
        vm.AlertText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Unreachable_only_matters_when_there_is_no_reading_to_fall_back_on()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.Update(null, IndicatorAlert.Unreachable, Now);
        vm.HasReading.Should().BeFalse();
        vm.AlertGlyph.Should().Be("⊘");

        vm.Update(Snapshot(61, 43, 88, stale: true), IndicatorAlert.Unreachable, MuchLater);
        vm.HasReading.Should().BeTrue("a stale number beats a symbol");
        vm.IsStale.Should().BeTrue();
    }

    [Fact]
    public void The_second_bar_is_how_much_of_the_window_has_elapsed_not_how_much_was_spent()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());

        // An hour left of five, and a day left of seven: the clock is far ahead of
        // the spend in the session and behind it in the week, which is the whole
        // point of drawing the two bars together.
        vm.Update(
            new UsageSnapshot(
                UsageWindow.Create(20, Now + TimeSpan.FromHours(1)),
                UsageWindow.Create(90, Now + TimeSpan.FromDays(1)),
                null,
                new Dictionary<string, UsageWindow>(),
                Now,
                false),
            IndicatorAlert.None,
            Now);

        vm.Session.HasTimeProgress.Should().BeTrue();
        vm.Session.TimePercent.Should().BeApproximately(80d, 0.001);
        vm.Session.Percent.Should().Be(20d, "the usage bar above it is unaffected");

        vm.Week.TimePercent.Should().BeApproximately(100d * 6 / 7, 0.001);
    }

    [Fact]
    public void Only_the_session_carries_a_clock_and_it_reads_h_mm()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.Update(
            new UsageSnapshot(
                UsageWindow.Create(29, Now + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(37)),
                UsageWindow.Create(58, Now + TimeSpan.FromDays(2)),
                UsageWindow.Create(88, Now + TimeSpan.FromDays(2)),
                new Dictionary<string, UsageWindow>(),
                Now,
                false),
            IndicatorAlert.None,
            Now);

        vm.Session.HasClock.Should().BeTrue();
        vm.Session.ClockText.Should().Be("(2:37)");

        vm.Week.HasClock.Should().BeFalse("48:00 is not a time anyone converts back into a day");
        vm.Week.ClockText.Should().BeEmpty();

        // The popup and the hover card were given no span, so they get no clock
        // either - they have room for the sentence.
        vm.TooltipSession.HasClock.Should().BeFalse();
        vm.TooltipSession.ResetText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Minutes_are_padded_so_the_clock_cannot_be_misread()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.Update(
            new UsageSnapshot(
                UsageWindow.Create(29, Now + TimeSpan.FromMinutes(5)),
                UsageWindow.Create(58, Now + TimeSpan.FromDays(2)),
                null,
                new Dictionary<string, UsageWindow>(),
                Now,
                false),
            IndicatorAlert.None,
            Now);

        vm.Session.ClockText.Should().Be("(0:05)", "5 would read as five hours");
    }

    [Fact]
    public void The_widget_splits_the_number_from_the_sign_and_the_popup_does_not()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.Update(Snapshot(29, 58, 88), IndicatorAlert.None, Now);

        vm.Session.PercentNumberText.Should().Be("29");
        vm.Session.PercentSign.Should().NotBeNullOrWhiteSpace();
        vm.Session.PercentText.Should().Be("29 %", "everywhere with room keeps the spacing its language asks for");

        vm.Update(null, IndicatorAlert.None, Now);
        vm.Session.Update(null, ThresholdState.Normal, Now);
        vm.Session.IsKnown.Should().BeFalse("the view hides the sign on this, not on an empty string");
        vm.Session.PercentNumberText.Should().Be(vm.Session.PercentText, "a dash is a dash either way");
    }

    [Fact]
    public void A_window_the_source_gave_no_reset_time_for_draws_no_time_bar()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.Update(
            new UsageSnapshot(
                UsageWindow.Create(20, null),
                UsageWindow.Create(30, null),
                null,
                new Dictionary<string, UsageWindow>(),
                Now,
                false),
            IndicatorAlert.None,
            Now);

        vm.Session.HasTimeProgress.Should().BeFalse();
        vm.Session.TimePercent.Should().Be(0d, "an empty bar, never a full one");
    }

    [Fact]
    public void More_time_left_than_the_window_is_long_shows_an_empty_bar_not_a_negative_one()
    {
        // A plan whose session window is not five hours. The assumption is wrong,
        // and the bar has to fail in the direction that claims nothing.
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.Update(
            new UsageSnapshot(
                UsageWindow.Create(20, Now + TimeSpan.FromHours(9)),
                UsageWindow.Create(30, Now + TimeSpan.FromDays(30)),
                null,
                new Dictionary<string, UsageWindow>(),
                Now,
                false),
            IndicatorAlert.None,
            Now);

        vm.Session.TimePercent.Should().Be(0d);
        vm.Week.TimePercent.Should().Be(0d);
    }

    [Fact]
    public void The_hover_card_and_the_popup_keep_a_countdown_and_no_time_bar()
    {
        // The elapsed share rests on an assumed window length; the countdown does
        // not. Only the widget's own bars make that assumption.
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.Update(Snapshot(20, 30, 40), IndicatorAlert.None, Now);

        vm.TooltipSession.HasTimeProgress.Should().BeFalse();
        vm.TooltipWeek.HasTimeProgress.Should().BeFalse();
        vm.TooltipSession.ResetText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void No_data_yet_is_a_dash_not_an_error()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.Update(null, IndicatorAlert.None, Now);

        vm.HasReading.Should().BeFalse();
        vm.AlertGlyph.Should().Be("—");
    }
}
