using ClaudeStatus.Sessions;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Which session changes reach the user, going by the settings.
/// </summary>
/// <remarks>
/// Three ways to say no, and they have to compose: the kind of change, the
/// per-session switch, and the setting for a session closing. The rule lives
/// apart from the controller precisely so all eight combinations can be stated
/// rather than reasoned about.
/// </remarks>
public class SessionNoticeTests
{
    private static AppSettings Settings(bool onEnd = false, params string[] muted)
        => new() { NotifyOnSessionEnd = onEnd, MutedSessions = muted };

    [Fact]
    public void A_finished_turn_is_announced_because_that_is_what_the_watch_is_for()
        => SessionNotices.ShouldAnnounce(SessionChange.Idle, "a", Settings()).Should().BeTrue();

    [Fact]
    public void A_session_closing_says_nothing_unless_it_was_asked_for()
    {
        // The user closed the window. Telling them what they have just done is the
        // definition of noise, which is why this one is off by default.
        SessionNotices.ShouldAnnounce(SessionChange.Finished, "a", Settings()).Should().BeFalse();
        SessionNotices.ShouldAnnounce(SessionChange.Finished, "a", Settings(onEnd: true)).Should().BeTrue();
    }

    [Fact]
    public void Asking_about_closings_does_not_change_the_other_notification()
        => SessionNotices.ShouldAnnounce(SessionChange.Idle, "a", Settings(onEnd: true)).Should().BeTrue();

    [Theory]
    [InlineData(SessionChange.Idle)]
    [InlineData(SessionChange.Finished)]
    public void A_silenced_session_is_silent_either_way(SessionChange change)
    {
        // The per-session switch is the stronger word: a session someone is sitting
        // in front of is not made interesting by being closed.
        SessionNotices.ShouldAnnounce(change, "a", Settings(onEnd: true, muted: "a")).Should().BeFalse();
        SessionNotices.ShouldAnnounce(change, "b", Settings(onEnd: true, muted: "a")).Should().BeTrue();
    }

    [Theory]
    [InlineData(SessionChange.None)]
    [InlineData(SessionChange.Appeared)]
    public void Nothing_else_is_worth_interrupting_for(SessionChange change)
    {
        // Appearing is not news - the user opened it.
        SessionNotices.ShouldAnnounce(change, "a", Settings(onEnd: true)).Should().BeFalse();
    }

    [Fact]
    public void The_default_settings_keep_closings_quiet()
        => new AppSettings().NotifyOnSessionEnd.Should().BeFalse(
            "a flag whose intended default is false is the one case that needs no inversion");

    [Fact]
    public void There_is_no_third_notification_to_configure()
    {
        // Guards the shape the config window describes: the watch announces a turn
        // ending and, if asked, a session closing. Anything else added to this enum
        // needs a decision here rather than falling through to silence unnoticed.
        Enum.GetValues<SessionChange>().Should().Equal(
            SessionChange.None, SessionChange.Appeared, SessionChange.Idle, SessionChange.Finished);
    }
}
