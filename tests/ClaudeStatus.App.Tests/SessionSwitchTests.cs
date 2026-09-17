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
