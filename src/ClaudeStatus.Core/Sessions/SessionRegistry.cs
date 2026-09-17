namespace ClaudeStatus.Sessions;

/// <summary>
/// What a session event changed, so the caller knows whether to say anything.
/// </summary>
public enum SessionChange
{
    /// <summary>Nothing worth reacting to.</summary>
    None = 0,

    /// <summary>A session the app had not seen before.</summary>
    Appeared = 1,

    /// <summary>A session finished a turn and is waiting for its user.</summary>
    Idle = 2,

    /// <summary>A session closed.</summary>
    Finished = 3,
}

/// <summary>
/// The sessions the app knows about, and how long it remembers them.
/// </summary>
/// <remarks>
/// <para>
/// Pure bookkeeping over events that arrive from somewhere else: no file
/// watching, no processes, no clock of its own - every method that needs the
/// time is handed it. That is what lets the whole of "which sessions are
/// running, and for how long" be tested without a running Claude Code.
/// </para>
/// <para>
/// It lists finished sessions as well as running ones, which is the only way
/// <see cref="Retention"/> means anything: a running session is running *now*,
/// so "sessions from the last hour" is a statement about ones that have since
/// stopped. They stay on the list, marked, until retention expires.
/// </para>
/// </remarks>
public sealed class SessionRegistry
{
    /// <summary>How long a finished session stays listed, unless told otherwise.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(1);

    /// <summary>The shortest retention worth offering.</summary>
    /// <remarks>
    /// Below this the list empties while the user is still looking at it, which
    /// reads as a bug rather than as a setting.
    /// </remarks>
    public static readonly TimeSpan MinimumRetention = TimeSpan.FromMinutes(5);

    /// <summary>The longest retention on offer.</summary>
    /// <remarks>
    /// A week. Past that this stops being "what is running" and becomes a
    /// history feature, which is a different thing and is not this.
    /// </remarks>
    public static readonly TimeSpan MaximumRetention = TimeSpan.FromDays(7);

    private readonly Dictionary<string, ClaudeSession> _sessions = new(StringComparer.Ordinal);

    /// <param name="retention">How long a finished session stays listed. Clamped.</param>
    public SessionRegistry(TimeSpan? retention = null)
    {
        Retention = Clamp(retention ?? DefaultRetention);
    }

    /// <summary>How long a finished session stays listed.</summary>
    public TimeSpan Retention { get; private set; }

    /// <summary>Brings <paramref name="retention"/> inside the offered range.</summary>
    public static TimeSpan Clamp(TimeSpan retention)
        => retention < MinimumRetention ? MinimumRetention
            : retention > MaximumRetention ? MaximumRetention
            : retention;

    /// <summary>Changes the retention and drops anything that no longer qualifies.</summary>
    public void SetRetention(TimeSpan retention, DateTimeOffset now)
    {
        Retention = Clamp(retention);
        Forget(now);
    }

    /// <summary>
    /// Records an event and says what it changed.
    /// </summary>
    /// <remarks>
    /// <see cref="SessionEventKind.Progressed"/> creates a session it has never
    /// seen, rather than ignoring it. Sessions outlive this app - one open when
    /// ClaudeStatus starts will never send a <c>Started</c>, and refusing to
    /// list it would mean the feature only ever worked for sessions opened in
    /// the right order.
    /// </remarks>
    public SessionChange Apply(SessionEvent report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (string.IsNullOrWhiteSpace(report.Id))
        {
            return SessionChange.None;
        }

        Forget(report.At);

        _sessions.TryGetValue(report.Id, out ClaudeSession? known);

        switch (report.Kind)
        {
            case SessionEventKind.Ended:
                if (known is null)
                {
                    return SessionChange.None;
                }

                _sessions[report.Id] = known with
                {
                    EndedAt = report.At,
                    LastSeenAt = report.At,
                    IsWorking = false,
                };
                return SessionChange.Finished;

            case SessionEventKind.Idled:
                if (known is null)
                {
                    _sessions[report.Id] = new ClaudeSession(
                        report.Id, report.Folder ?? string.Empty, report.At, report.At, null, false, report.Origin);
                    return SessionChange.None;
                }

                // Nothing to announce: after an ordinary turn Stop already did, and
                // after an interrupt the user is the one who just stopped it. This
                // only corrects the state.
                _sessions[report.Id] = known with
                {
                    LastSeenAt = report.At,
                    IsWorking = false,
                    EndedAt = null,
                    Origin = SessionOrigin.Merge(known.Origin, report.Origin),
                };
                return SessionChange.None;

            case SessionEventKind.Started:
            case SessionEventKind.Submitted:
            case SessionEventKind.Progressed:

                // Only a submitted prompt means work is happening. A turn ending
                // means the opposite, and a session opening means neither - it is
                // sitting at an empty prompt.
                bool working = report.Kind == SessionEventKind.Submitted;

                if (known is null)
                {
                    _sessions[report.Id] = new ClaudeSession(
                        report.Id, report.Folder ?? string.Empty, report.At, report.At, null, working, report.Origin);

                    // A first sighting is worth noticing; a turn ending is worth
                    // announcing. Starting is neither - nobody needs telling that
                    // the thing they just launched has launched - and a turn
                    // beginning is something the user is watching happen.
                    return report.Kind switch
                    {
                        SessionEventKind.Progressed => SessionChange.Idle,
                        SessionEventKind.Submitted => SessionChange.None,
                        _ => SessionChange.Appeared,
                    };
                }

                _sessions[report.Id] = known with
                {
                    LastSeenAt = report.At,
                    IsWorking = working,

                    // A session that reports again after ending is a reused id or
                    // a resumed session; either way it is running now.
                    EndedAt = null,

                    // The folder can legitimately change: /cwd, or a session
                    // resumed elsewhere. The newest report wins.
                    Folder = string.IsNullOrEmpty(report.Folder) ? known.Folder : report.Folder,

                    // Not simply the newest: a turn ending while the user is in
                    // another app knows less about the window than the prompt they
                    // typed into it. See SessionOrigin.Merge.
                    Origin = SessionOrigin.Merge(known.Origin, report.Origin),
                };

                return report.Kind == SessionEventKind.Progressed
                    ? SessionChange.Idle
                    : SessionChange.None;

            default:
                return SessionChange.None;
        }
    }

    /// <summary>The sessions worth showing, running first, then most recent.</summary>
    /// <remarks>
    /// Running before finished because the list answers "what is going on" before
    /// it answers "what went on". Within each group, most recently active first.
    /// </remarks>
    public IReadOnlyList<ClaudeSession> Snapshot(DateTimeOffset now)
    {
        Forget(now);
        return [.. _sessions.Values
            .OrderByDescending(s => s.IsRunning)
            .ThenByDescending(s => s.LastSeenAt)
            .ThenBy(s => s.Id, StringComparer.Ordinal)];
    }

    /// <summary>The one session with this id, or null.</summary>
    public ClaudeSession? Find(string id)
        => id is not null && _sessions.TryGetValue(id, out ClaudeSession? found) ? found : null;

    /// <summary>Drops everything.</summary>
    public void Clear() => _sessions.Clear();

    /// <summary>
    /// Puts back a remembered list, dropping anything retention no longer covers.
    /// </summary>
    /// <remarks>
    /// Restoring, not merging: this runs before anything is observed, so there is
    /// nothing to merge with. Sessions past their retention are not restored at
    /// all - a list reloaded at breakfast should not still be showing yesterday
    /// afternoon.
    /// </remarks>
    public void Restore(IReadOnlyList<ClaudeSession> sessions, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions.Clear();
        foreach (ClaudeSession session in sessions)
        {
            if (!string.IsNullOrWhiteSpace(session.Id) && session.ForgetAfter(Retention) > now)
            {
                _sessions[session.Id] = session;
            }
        }
    }

    /// <summary>Drops sessions past their retention.</summary>
    private void Forget(DateTimeOffset now)
    {
        if (_sessions.Count == 0)
        {
            return;
        }

        foreach (string id in _sessions
            .Where(pair => pair.Value.ForgetAfter(Retention) <= now)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _sessions.Remove(id);
        }
    }
}
