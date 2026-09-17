using ClaudeStatus.Sessions;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Which Claude Code sessions the app thinks are running, and for how long it
/// keeps saying so.
/// </summary>
public class SessionRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 9, 0, 0, TimeSpan.Zero);

    private static SessionEvent Event(SessionEventKind kind, string id, int minutes, string folder = "C:\\Proyectos\\Alpha")
        => new(kind, id, folder, T0.AddMinutes(minutes));

    [Fact]
    public void A_started_session_is_listed_as_running()
    {
        var registry = new SessionRegistry();

        registry.Apply(Event(SessionEventKind.Started, "a", 0)).Should().Be(SessionChange.Appeared);

        ClaudeSession session = registry.Snapshot(T0.AddMinutes(1)).Should().ContainSingle().Subject;
        session.Id.Should().Be("a");
        session.IsRunning.Should().BeTrue();
        session.Name.Should().Be("Alpha", "the folder's leaf is what a person recognises");
    }

    [Fact]
    public void A_turn_ending_is_what_is_worth_announcing()
    {
        var registry = new SessionRegistry();
        registry.Apply(Event(SessionEventKind.Started, "a", 0));

        registry.Apply(Event(SessionEventKind.Progressed, "a", 5))
            .Should().Be(SessionChange.Idle, "the session is now waiting for its user");
    }

    [Fact]
    public void Starting_is_not_worth_announcing()
    {
        // Nobody needs telling that the thing they just launched has launched.
        new SessionRegistry().Apply(Event(SessionEventKind.Started, "a", 0))
            .Should().Be(SessionChange.Appeared);
    }

    [Fact]
    public void A_session_already_open_when_the_app_starts_is_still_picked_up()
    {
        // It opened before ClaudeStatus did, so its Started was never seen. The
        // first thing we hear is a turn ending, and refusing to list it would
        // mean the feature only worked for sessions opened in the right order.
        var registry = new SessionRegistry();

        registry.Apply(Event(SessionEventKind.Progressed, "a", 0)).Should().Be(SessionChange.Idle);

        registry.Snapshot(T0).Should().ContainSingle().Which.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void An_ended_session_stays_listed_but_stops_being_running()
    {
        var registry = new SessionRegistry();
        registry.Apply(Event(SessionEventKind.Started, "a", 0));

        registry.Apply(Event(SessionEventKind.Ended, "a", 10)).Should().Be(SessionChange.Finished);

        ClaudeSession session = registry.Snapshot(T0.AddMinutes(11)).Should().ContainSingle().Subject;
        session.IsRunning.Should().BeFalse();
        session.Duration(T0.AddMinutes(30)).Should().Be(TimeSpan.FromMinutes(10), "it stopped at ten past");
    }

    [Fact]
    public void Ending_a_session_nobody_reported_changes_nothing()
    {
        new SessionRegistry().Apply(Event(SessionEventKind.Ended, "ghost", 0))
            .Should().Be(SessionChange.None);
    }

    [Fact]
    public void A_finished_session_drops_off_once_retention_expires()
    {
        var registry = new SessionRegistry(TimeSpan.FromMinutes(30));
        registry.Apply(Event(SessionEventKind.Started, "a", 0));
        registry.Apply(Event(SessionEventKind.Ended, "a", 10));

        registry.Snapshot(T0.AddMinutes(39)).Should().ContainSingle();
        registry.Snapshot(T0.AddMinutes(41)).Should().BeEmpty();
    }

    [Fact]
    public void A_session_killed_without_a_goodbye_drops_off_the_same_way()
    {
        // Close the terminal and SessionEnd never runs. There is no process check
        // and no liveness probe - it simply stops saying anything, and retention
        // does the rest.
        var registry = new SessionRegistry(TimeSpan.FromMinutes(30));
        registry.Apply(Event(SessionEventKind.Progressed, "a", 0));

        registry.Snapshot(T0.AddMinutes(29)).Should().ContainSingle().Which.IsRunning.Should().BeTrue();
        registry.Snapshot(T0.AddMinutes(31)).Should().BeEmpty();
    }

    [Fact]
    public void A_busy_session_is_never_forgotten()
    {
        var registry = new SessionRegistry(TimeSpan.FromMinutes(30));
        for (int m = 0; m <= 120; m += 10)
        {
            registry.Apply(Event(SessionEventKind.Progressed, "a", m));
        }

        registry.Snapshot(T0.AddMinutes(125)).Should().ContainSingle();
    }

    [Fact]
    public void Running_sessions_are_listed_before_finished_ones()
    {
        var registry = new SessionRegistry();
        registry.Apply(Event(SessionEventKind.Started, "old", 0, "C:\\Proyectos\\Old"));
        registry.Apply(Event(SessionEventKind.Ended, "old", 1, "C:\\Proyectos\\Old"));
        registry.Apply(Event(SessionEventKind.Started, "live", 2, "C:\\Proyectos\\Live"));

        // "What is going on" before "what went on", even though the finished one
        // is not the older of the two by start time.
        registry.Snapshot(T0.AddMinutes(3)).Select(s => s.Id).Should().Equal("live", "old");
    }

    [Fact]
    public void A_resumed_session_counts_as_running_again()
    {
        var registry = new SessionRegistry();
        registry.Apply(Event(SessionEventKind.Started, "a", 0));
        registry.Apply(Event(SessionEventKind.Ended, "a", 5));

        registry.Apply(Event(SessionEventKind.Progressed, "a", 6)).Should().Be(SessionChange.Idle);

        registry.Snapshot(T0.AddMinutes(7)).Should().ContainSingle().Which.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void The_newest_folder_wins_because_a_session_can_move()
    {
        var registry = new SessionRegistry();
        registry.Apply(Event(SessionEventKind.Started, "a", 0, "C:\\Proyectos\\Before"));
        registry.Apply(Event(SessionEventKind.Progressed, "a", 5, "C:\\Proyectos\\After"));

        registry.Snapshot(T0.AddMinutes(6)).Should().ContainSingle().Which.Name.Should().Be("After");
    }

    [Fact]
    public void An_empty_folder_does_not_erase_the_one_already_known()
    {
        var registry = new SessionRegistry();
        registry.Apply(Event(SessionEventKind.Started, "a", 0, "C:\\Proyectos\\Alpha"));
        registry.Apply(Event(SessionEventKind.Progressed, "a", 5, string.Empty));

        registry.Snapshot(T0.AddMinutes(6)).Should().ContainSingle().Which.Name.Should().Be("Alpha");
    }

    [Fact]
    public void A_posix_folder_from_a_windows_process_still_reads()
    {
        // Observed on this machine: Claude Code reporting /mnt/c/... paths.
        var registry = new SessionRegistry();
        registry.Apply(Event(SessionEventKind.Started, "a", 0, "/mnt/c/Proyectos/React/Elitechip-Core"));

        registry.Snapshot(T0).Should().ContainSingle().Which.Name.Should().Be("Elitechip-Core");
    }

    [Fact]
    public void A_trailing_separator_does_not_produce_a_nameless_session()
    {
        new ClaudeSession("a", "C:\\Proyectos\\Alpha\\", T0, T0).Name.Should().Be("Alpha");
    }

    [Fact]
    public void An_event_without_an_id_is_ignored()
    {
        var registry = new SessionRegistry();

        registry.Apply(new SessionEvent(SessionEventKind.Started, "  ", "C:\\x", T0))
            .Should().Be(SessionChange.None);
        registry.Snapshot(T0).Should().BeEmpty();
    }

    [Fact]
    public void Retention_is_clamped_to_something_usable()
    {
        new SessionRegistry(TimeSpan.FromSeconds(1)).Retention
            .Should().Be(SessionRegistry.MinimumRetention, "a list that empties while you read it looks broken");

        new SessionRegistry(TimeSpan.FromDays(400)).Retention
            .Should().Be(SessionRegistry.MaximumRetention, "past a week this is a history feature, which it is not");
    }

    [Fact]
    public void Shortening_the_retention_drops_what_no_longer_qualifies()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(6));
        registry.Apply(Event(SessionEventKind.Started, "a", 0));
        registry.Apply(Event(SessionEventKind.Ended, "a", 1));

        registry.SetRetention(TimeSpan.FromMinutes(10), T0.AddHours(1));

        registry.Snapshot(T0.AddHours(1)).Should().BeEmpty();
    }
}
