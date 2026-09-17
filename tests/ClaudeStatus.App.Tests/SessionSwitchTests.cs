using ClaudeStatus.App.ViewModels;
using ClaudeStatus.Localization;
using ClaudeStatus.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// The popup's session switches: they report the user's flips, and only those.
/// </summary>
/// <remarks>
/// The failure worth guarding is the echo. The list is rebuilt from settings on
/// every session change, and a rebuild that read as the user flipping switches
/// would write the settings back, or turn the watch off, on its own.
/// </remarks>
public sealed class SessionSwitchTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly Localizer L = TestLocalizer.English();

    private readonly UsageMonitor _monitor;
    private AppSettings _settings = new();

    public SessionSwitchTests()
    {
        var clock = new FakeTimeProvider(Now);
        _monitor = new UsageMonitor(new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);
    }

    public void Dispose() => _monitor.Dispose();

    private DetailsViewModel Build() => new(_monitor, () => _settings, L, new FakeTimeProvider(Now));

    private static ClaudeSession Session(string id) => new(id, "C:\\Proyectos\\" + id, Now, Now);

    private static readonly SessionOrigin Terminal = new(4242, Now, 0, SessionOriginPrecision.Process);

    private static ClaudeSession Focusable(string id) => Session(id) with { Origin = Terminal };

    [Fact]
    public void Only_a_running_session_with_a_known_terminal_can_be_focused()
    {
        using DetailsViewModel details = Build();

        details.ApplySessions(
            [Focusable("a"), Session("b"), Focusable("c") with { EndedAt = Now }],
            Now);

        details.Sessions.Select(s => s.CanFocus).Should().Equal(true, false, false);
    }

    [Fact]
    public void Clicking_a_row_asks_to_focus_that_session_and_touches_no_switch()
    {
        using DetailsViewModel details = Build();
        var focused = new List<string>();
        var muted = new List<SessionMuteChangedEventArgs>();
        details.SessionFocusRequested += (_, id) => focused.Add(id);
        details.SessionMuteChanged += (_, e) => muted.Add(e);
        details.ApplySessions([Focusable("a"), Focusable("b")], Now);

        details.FocusSessionCommand.Execute(details.Sessions[1]);

        focused.Should().Equal("b");
        muted.Should().BeEmpty();
        details.Sessions.Select(s => s.Notifies).Should().Equal(true, true);
    }

    [Fact]
    public void A_row_that_cannot_be_focused_asks_for_nothing()
    {
        using DetailsViewModel details = Build();
        var focused = new List<string>();
        details.SessionFocusRequested += (_, id) => focused.Add(id);
        details.ApplySessions([Session("a")], Now);

        details.FocusSessionCommand.Execute(details.Sessions[0]);
        details.FocusSessionCommand.Execute(null);

        focused.Should().BeEmpty();
    }

    [Fact]
    public void Flipping_a_switch_does_not_ask_to_focus()
    {
        using DetailsViewModel details = Build();
        var focused = new List<string>();
        details.SessionFocusRequested += (_, id) => focused.Add(id);
        details.ApplySessions([Focusable("a")], Now);

        details.Sessions[0].Notifies = false;

        focused.Should().BeEmpty();
    }

    [Fact]
    public void A_row_switch_is_off_for_a_silenced_session_and_building_the_list_reports_nothing()
    {
        _settings = new AppSettings { MutedSessions = ["b"] };
        using DetailsViewModel details = Build();
        var reported = new List<SessionMuteChangedEventArgs>();
        details.SessionMuteChanged += (_, e) => reported.Add(e);

        details.ApplySessions([Session("a"), Session("b")], Now);

        details.Sessions.Select(s => s.Notifies).Should().Equal(true, false);
        reported.Should().BeEmpty();
    }

    [Fact]
    public void Flipping_a_row_switch_reports_that_session()
    {
        using DetailsViewModel details = Build();
        var reported = new List<SessionMuteChangedEventArgs>();
        details.SessionMuteChanged += (_, e) => reported.Add(e);
        details.ApplySessions([Session("a"), Session("b")], Now);

        details.Sessions[1].Notifies = false;

        reported.Should().ContainSingle();
        reported[0].SessionId.Should().Be("b");
        reported[0].IsMuted.Should().BeTrue();
    }

    [Fact]
    public void A_replaced_row_no_longer_reports()
    {
        using DetailsViewModel details = Build();
        var reported = new List<SessionMuteChangedEventArgs>();
        details.SessionMuteChanged += (_, e) => reported.Add(e);
        details.ApplySessions([Session("a")], Now);
        SessionRowViewModel old = details.Sessions[0];

        details.ApplySessions([Session("a")], Now);
        old.Notifies = false;

        reported.Should().BeEmpty();
    }

    [Fact]
    public void The_watch_switch_reports_a_flip_but_not_the_settings_being_shown()
    {
        using DetailsViewModel details = Build();
        var reported = new List<bool>();
        details.SessionWatchChanged += (_, on) => reported.Add(on);

        details.ApplySessionWatch(true);
        details.ApplySessionWatch(false);
        reported.Should().BeEmpty();

        details.SessionWatch = true;
        reported.Should().Equal(true);
    }

    [Fact]
    public void Turning_the_watch_off_empties_the_list()
    {
        using DetailsViewModel details = Build();
        details.ShowSessions = true;
        details.ApplySessions([Session("a")], Now);

        details.ApplySessionWatch(false);

        details.ShowSessions.Should().BeFalse();
        details.Sessions.Should().BeEmpty();
        details.HasSessions.Should().BeFalse();
    }
}
