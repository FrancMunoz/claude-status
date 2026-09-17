using ClaudeStatus.Sessions;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Which window a session's notification should bring back, and how that survives
/// the trip from the hook process through the spool and across restarts.
/// </summary>
public sealed class SessionOriginTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TerminalStarted = new(2026, 9, 16, 8, 0, 0, 123, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ClaudeStatus.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SessionOrigin Origin(int pid, long window, SessionOriginPrecision precision, DateTimeOffset? started = null)
        => new(pid, started ?? TerminalStarted, window, precision);

    [Fact]
    public void A_weaker_guess_about_the_same_terminal_does_not_replace_a_stronger_one()
    {
        SessionOrigin typedInto = Origin(100, 0x10, SessionOriginPrecision.Foreground);
        SessionOrigin someWindow = Origin(100, 0x20, SessionOriginPrecision.Process);

        SessionOrigin.Merge(typedInto, someWindow)
            .Should().Be(typedInto, "a turn ending while the user is elsewhere knows less than the prompt they typed");
    }

    [Fact]
    public void An_equal_or_better_guess_about_the_same_terminal_wins()
    {
        SessionOrigin old = Origin(100, 0x10, SessionOriginPrecision.Foreground);
        SessionOrigin moved = Origin(100, 0x30, SessionOriginPrecision.Foreground);

        SessionOrigin.Merge(old, moved).Should().Be(moved, "the tab was dragged into another window of the same terminal");
        SessionOrigin.Merge(Origin(100, 0x10, SessionOriginPrecision.Process), old).Should().Be(old);
    }

    [Fact]
    public void A_different_terminal_process_always_wins()
    {
        SessionOrigin before = Origin(100, 0x10, SessionOriginPrecision.Foreground);
        SessionOrigin resumed = Origin(200, 0x40, SessionOriginPrecision.Process);

        SessionOrigin.Merge(before, resumed).Should().Be(resumed, "the session was resumed somewhere else");

        // A recycled pid is a different process too.
        SessionOrigin recycled = Origin(100, 0x50, SessionOriginPrecision.Process, TerminalStarted.AddHours(1));
        SessionOrigin.Merge(before, recycled).Should().Be(recycled);
    }

    [Fact]
    public void No_new_origin_keeps_the_known_one()
    {
        SessionOrigin known = Origin(100, 0x10, SessionOriginPrecision.Console);

        SessionOrigin.Merge(known, null).Should().Be(known);
        SessionOrigin.Merge(null, null).Should().BeNull();
    }

    [Fact]
    public void The_registry_keeps_the_best_origin_across_turns()
    {
        var registry = new SessionRegistry();
        SessionOrigin typedInto = Origin(100, 0x10, SessionOriginPrecision.Foreground);

        registry.Apply(new SessionEvent(SessionEventKind.Started, "a", "C:\\A", T0, Origin(100, 0x20, SessionOriginPrecision.Process)));
        registry.Apply(new SessionEvent(SessionEventKind.Submitted, "a", "C:\\A", T0.AddMinutes(1), typedInto));
        registry.Apply(new SessionEvent(SessionEventKind.Progressed, "a", "C:\\A", T0.AddMinutes(9), Origin(100, 0x20, SessionOriginPrecision.Process)));
        registry.Apply(new SessionEvent(SessionEventKind.Progressed, "a", "C:\\A", T0.AddMinutes(10)));

        registry.Find("a")!.Origin.Should().Be(typedInto);
    }

    [Fact]
    public void The_spool_carries_the_origin_exactly()
    {
        var spool = new SessionSpool(_directory);
        SessionOrigin origin = Origin(4242, 0x0001_0000_0ABCL, SessionOriginPrecision.Console);

        spool.Write(new SessionEvent(SessionEventKind.Progressed, "a", "C:\\A", T0, origin));

        spool.Drain(T0).Should().ContainSingle()
            .Which.Origin.Should().Be(origin, "the start time is compared tick for tick before a window is focused");
    }

    [Fact]
    public void A_spooled_event_without_an_origin_still_arrives()
    {
        var spool = new SessionSpool(_directory);

        spool.Write(new SessionEvent(SessionEventKind.Progressed, "a", "C:\\A", T0));

        spool.Drain(T0).Should().ContainSingle().Which.Origin.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 16, 2)]
    [InlineData(-5, 16, 2)]
    [InlineData(100, 0, 2)]
    [InlineData(100, 16, 99)]
    public void An_implausible_spooled_origin_becomes_none(int pid, long window, int precision)
    {
        var spool = new SessionSpool(_directory);
        spool.Ensure();

        // Written by hand: this file crosses a process boundary, and anything
        // running as this user can put one there.
        File.WriteAllText(
            Path.Combine(spool.Directory, "forged.json"),
            $$$"""{"Kind":"Progressed","Id":"a","Folder":"C:\\A","At":"2026-09-16T09:00:00+00:00","Origin":{"ProcessId":{{{pid}}},"ProcessStartedAt":"2026-09-16T08:00:00+00:00","Window":{{{window}}},"Precision":{{{precision}}}}}""");

        spool.Drain(T0).Should().ContainSingle().Which.Origin.Should().BeNull();
    }

    [Fact]
    public void The_store_remembers_the_origin_across_restarts()
    {
        var store = new SessionStore(_directory);
        SessionOrigin origin = Origin(4242, 0x77, SessionOriginPrecision.Foreground);

        store.Save([new ClaudeSession("a", "C:\\A", T0, T0, Origin: origin)]);

        store.Load().Should().ContainSingle().Which.Origin.Should().Be(origin);
    }
}
