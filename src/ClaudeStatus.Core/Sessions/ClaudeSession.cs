namespace ClaudeStatus.Sessions;

/// <summary>
/// What happened to a Claude Code session, as reported by one of our hooks.
/// </summary>
/// <remarks>
/// Deliberately three verbs and not the hook event names. The hooks are Claude
/// Code's vocabulary and can change; this is ours, and the mapping between them
/// lives in exactly one place so a renamed event is a one-line edit rather than
/// a search through the app.
/// </remarks>
public enum SessionEventKind
{
    /// <summary>A session opened.</summary>
    Started = 0,

    /// <summary>A turn has just begun: the user pressed Enter and Claude is working.</summary>
    /// <remarks>
    /// The only signal that a session is busy rather than merely alive. Without
    /// it the app hears from a session only when a turn ends, so every session
    /// that had ever spoken looked equally "running" - including the one the
    /// toast had just said was waiting for its user.
    /// </remarks>
    Submitted = 3,

    /// <summary>A session is still there, and has just finished a turn.</summary>
    /// <remarks>
    /// The heartbeat. It is also the only signal a session that started before
    /// the app did ever gives, so it has to be able to create a session as well
    /// as refresh one.
    /// </remarks>
    Progressed = 1,

    /// <summary>A session closed.</summary>
    Ended = 2,
}

/// <summary>What a session is doing right now.</summary>
public enum SessionState
{
    /// <summary>A turn is in progress.</summary>
    Working = 0,

    /// <summary>Open, and waiting for its user.</summary>
    Waiting = 1,

    /// <summary>Closed.</summary>
    Finished = 2,
}

/// <summary>One report from a hook.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Id">Claude Code's session id. The identity; never shown.</param>
/// <param name="Folder">The session's working directory. What the user recognises it by.</param>
/// <param name="At">When it happened.</param>
/// <param name="Origin">The window the session runs in, when the hook could find it.</param>
public sealed record SessionEvent(
    SessionEventKind Kind,
    string Id,
    string Folder,
    DateTimeOffset At,
    SessionOrigin? Origin = null);

/// <summary>
/// A Claude Code session the app knows about.
/// </summary>
/// <param name="Id">Claude Code's session id.</param>
/// <param name="Folder">Its working directory.</param>
/// <param name="StartedAt">When it was first seen - not necessarily when it opened.</param>
/// <param name="LastSeenAt">When it last finished a turn.</param>
/// <param name="EndedAt">When it closed, or null while it is still running.</param>
/// <param name="IsWorking">Whether a turn is in progress.</param>
/// <param name="Origin">The window it runs in, for focusing it from a notification.</param>
/// <remarks>
/// <see cref="StartedAt"/> is honest about being a first sighting. A session that
/// was already open when ClaudeStatus launched is first seen at the end of its
/// next turn, and claiming that as its start time would be a lie the UI would
/// then repeat as a duration.
/// </remarks>
public sealed record ClaudeSession(
    string Id,
    string Folder,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? EndedAt = null,
    bool IsWorking = false,
    SessionOrigin? Origin = null)
{
    /// <summary>Whether the session is still open.</summary>
    public bool IsRunning => EndedAt is null;

    /// <summary>
    /// What the session is actually doing.
    /// </summary>
    /// <remarks>
    /// The distinction the list was missing. "Open" and "busy" are not the same
    /// thing, and a list that calls both of them running contradicts the very
    /// notification that told the user their session was waiting for them.
    /// </remarks>
    public SessionState State => EndedAt is not null
        ? SessionState.Finished
        : IsWorking ? SessionState.Working : SessionState.Waiting;

    /// <summary>
    /// The last folder segment - what the user actually recognises.
    /// </summary>
    /// <remarks>
    /// Separators are handled by hand rather than with <c>Path.GetFileName</c>:
    /// the folder arrives from another process's idea of the filesystem, and on
    /// this machine Claude Code has already been observed reporting WSL paths
    /// (<c>/mnt/c/...</c>) from a Windows process. A path the host does not
    /// recognise must still produce a readable name.
    /// </remarks>
    public string Name
    {
        get
        {
            string trimmed = (Folder ?? string.Empty).TrimEnd('/', '\\');
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            int cut = trimmed.LastIndexOfAny(['/', '\\']);
            return cut < 0 ? trimmed : trimmed[(cut + 1)..];
        }
    }

    /// <summary>How long it ran, or has been running.</summary>
    public TimeSpan Duration(DateTimeOffset now)
    {
        DateTimeOffset until = EndedAt ?? now;
        return until > StartedAt ? until - StartedAt : TimeSpan.Zero;
    }

    /// <summary>When this session stops being worth listing.</summary>
    /// <remarks>
    /// Measured from whichever came last, the end or the last turn - so a
    /// finished session stays listed for the retention window, and one that was
    /// killed without ever reporting an end drops off the same way once it goes
    /// quiet. That is the whole orphan story: no process checking, no liveness
    /// probe, just a session that stopped saying anything.
    /// </remarks>
    public DateTimeOffset ForgetAfter(TimeSpan retention) => (EndedAt ?? LastSeenAt) + retention;
}
