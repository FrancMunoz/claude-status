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
    public void No_data_yet_is_a_dash_not_an_error()
    {
        var vm = new TaskbarWidgetViewModel(new Localizer());

        vm.Update(null, IndicatorAlert.None, Now);

        vm.HasReading.Should().BeFalse();
        vm.AlertGlyph.Should().Be("—");
    }
}
