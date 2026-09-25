namespace ClaudeStatus.Sessions;

/// <summary>
/// How many Claude Code sessions are busy right now, as the indicators show it.
/// </summary>
/// <remarks>
/// Not simply a count of <see cref="ClaudeSession.IsWorking"/>. A turn is marked
/// working when its prompt is submitted and cleared when it ends, and a session
/// killed mid-turn - the terminal closed, the machine slept through it - never
/// sends the end. Counted naively it would pulse on the taskbar forever.
/// </remarks>
public static class SessionActivity
{
    /// <summary>
    /// How long a turn may go without a word before it stops counting as working.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. The hooks report only a turn's start and end, so a
    /// long agentic turn is silent for its whole length, and cutting it short
    /// would hide exactly the run the user most wants to keep an eye on. A dead
    /// session showing as busy for a while is the cheaper mistake.
    /// </remarks>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromHours(2);

    /// <summary>The number of open sessions with a turn in progress.</summary>
    public static int CountWorking(IReadOnlyList<ClaudeSession> sessions, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        int count = 0;
        foreach (ClaudeSession session in sessions)
        {
            if (IsWorking(session, now))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The number of open sessions, busy or not.</summary>
    /// <remarks>
    /// The denominator of the indicators' <c>1/3</c>. Open means no end was
    /// reported: a session killed without one stays counted until the retention
    /// window drops it from the list, the same way it stays listed. No
    /// <see cref="StuckAfter"/> here - that rule is about a turn, not a session.
    /// </remarks>
    public static int CountOpen(IReadOnlyList<ClaudeSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        int count = 0;
        foreach (ClaudeSession session in sessions)
        {
            if (session.IsRunning)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Whether this session counts as busy at <paramref name="now"/>.</summary>
    public static bool IsWorking(ClaudeSession session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.State == SessionState.Working && now - session.LastSeenAt < StuckAfter;
    }
}
