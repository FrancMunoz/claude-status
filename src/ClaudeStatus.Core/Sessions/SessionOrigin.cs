namespace ClaudeStatus.Sessions;

/// <summary>How sure the hook was about which window a session lives in.</summary>
/// <remarks>
/// Ordered: a higher value is better evidence, and <see cref="SessionRegistry"/>
/// never lets a weaker guess replace a stronger one for the same terminal.
/// </remarks>
public enum SessionOriginPrecision
{
    /// <summary>The first visible window of an ancestor process - right process, maybe the wrong window.</summary>
    Process = 0,

    /// <summary>The window of the console the session runs in, or the terminal that owns it.</summary>
    Console = 1,

    /// <summary>
    /// The window that had the foreground when the hook ran, and it belongs to the session's terminal.
    /// </summary>
    /// <remarks>
    /// The best evidence there is. A prompt is submitted by pressing Enter in that
    /// very window, so at <c>UserPromptSubmit</c> this is the exact window even when
    /// the terminal process owns several.
    /// </remarks>
    Foreground = 2,
}

/// <summary>
/// The window a Claude Code session was running in, so a click on its notification
/// can bring that window back.
/// </summary>
/// <param name="ProcessId">The process that owns <paramref name="Window"/> - the terminal or editor, not Claude Code.</param>
/// <param name="ProcessStartedAt">
/// When that process started. A pid is reused once its process exits; the pair is
/// what identifies a process, and a window is only focused when both still match.
/// </param>
/// <param name="Window">
/// The native window handle, widened to 64 bits. Opaque outside the platform code.
/// Zero where the platform identifies only the terminal application (macOS), which
/// is only valid with <see cref="SessionOriginPrecision.Process"/>.
/// </param>
/// <param name="Precision">How the window was found.</param>
/// <remarks>
/// Not a secret and not trusted either: it crosses from the hook process through a
/// file anybody running as this user could write. The platform code re-validates
/// all of it before acting, and the worst a forged one can do is focus a window
/// that is already on this user's desktop.
/// </remarks>
public sealed record SessionOrigin(
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    long Window,
    SessionOriginPrecision Precision)
{
    /// <summary>Whether this origin names the same terminal process as <paramref name="other"/>.</summary>
    public bool IsSameProcess(SessionOrigin? other)
        => other is not null
            && other.ProcessId == ProcessId
            && other.ProcessStartedAt == ProcessStartedAt;

    /// <summary>
    /// Picks which of two origins to keep for one session.
    /// </summary>
    /// <remarks>
    /// The newer one wins unless it is a weaker guess about the same terminal. A
    /// <c>Stop</c> fired while the user is in another app can only find "some
    /// window of the terminal", and must not overwrite the exact window the
    /// preceding <c>UserPromptSubmit</c> saw. A different process always wins: the
    /// session was resumed somewhere else.
    /// </remarks>
    public static SessionOrigin? Merge(SessionOrigin? known, SessionOrigin? reported)
    {
        if (reported is null)
        {
            return known;
        }

        if (known is null || !known.IsSameProcess(reported))
        {
            return reported;
        }

        return reported.Precision >= known.Precision ? reported : known;
    }
}
