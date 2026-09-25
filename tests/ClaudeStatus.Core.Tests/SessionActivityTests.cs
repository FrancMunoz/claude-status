using ClaudeStatus.Sessions;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Which sessions the indicators show as busy.
/// </summary>
public class SessionActivityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);

    private static ClaudeSession Session(string id, bool working, DateTimeOffset? lastSeen = null, DateTimeOffset? ended = null)
        => new(id, "C:\\Proyectos\\" + id, T0, lastSeen ?? T0, ended, working);

    [Fact]
    public void Only_sessions_with_a_turn_in_progress_are_counted()
    {
        IReadOnlyList<ClaudeSession> sessions =
        [
            Session("a", working: true),
            Session("b", working: false),
            Session("c", working: true),
        ];

        SessionActivity.CountWorking(sessions, T0.AddMinutes(5)).Should().Be(2);
    }

    [Fact]
    public void Nothing_is_working_in_an_empty_list()
        => SessionActivity.CountWorking([], T0).Should().Be(0);

    [Fact]
    public void Open_counts_every_session_without_an_end_busy_or_not()
    {
        IReadOnlyList<ClaudeSession> sessions =
        [
            Session("a", working: true),
            Session("b", working: false),
            Session("c", working: false, ended: T0.AddMinutes(1)),
        ];

        SessionActivity.CountOpen(sessions).Should().Be(2, "a finished session is listed for a while but no longer open");
        SessionActivity.CountOpen([]).Should().Be(0);
    }

    [Fact]
    public void A_session_that_went_quiet_mid_turn_stays_open_after_it_stops_counting_as_busy()
    {
        // The badge reads 0/1, not 0/0: the turn has been given up on, the session
        // has not. It leaves the list when the retention window says so.
        IReadOnlyList<ClaudeSession> sessions = [Session("a", working: true)];
        DateTimeOffset later = T0 + SessionActivity.StuckAfter;

        SessionActivity.CountWorking(sessions, later).Should().Be(0);
        SessionActivity.CountOpen(sessions).Should().Be(1);
    }

    [Fact]
    public void A_finished_session_is_not_working_even_if_its_last_turn_never_ended()
    {
        ClaudeSession session = Session("a", working: true, ended: T0.AddMinutes(1));

        SessionActivity.IsWorking(session, T0.AddMinutes(2)).Should().BeFalse();
    }

    [Fact]
    public void A_long_silent_turn_still_counts_until_it_has_been_quiet_for_too_long()
    {
        ClaudeSession session = Session("a", working: true);

        SessionActivity.IsWorking(session, T0 + SessionActivity.StuckAfter - TimeSpan.FromMinutes(1))
            .Should().BeTrue("an agentic turn is silent for its whole length");
        SessionActivity.IsWorking(session, T0 + SessionActivity.StuckAfter)
            .Should().BeFalse("a session killed mid-turn must not show as busy forever");
    }
}
