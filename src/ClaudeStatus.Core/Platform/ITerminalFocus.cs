using ClaudeStatus.Sessions;

namespace ClaudeStatus.Platform;

/// <summary>
/// Finds the window a Claude Code session is running in, and brings it back.
/// </summary>
/// <remarks>
/// <para>
/// Two halves that run in two different processes. <see cref="Capture"/> runs in
/// the short-lived hook process, because only there is the session's process tree
/// still intact: the hook's parent is Claude Code, and above it the shell and the
/// terminal. By the time the app reads the spool the hook has exited and the chain
/// is gone. <see cref="TryFocus"/> runs in the app, when the user clicks the
/// notification.
/// </para>
/// <para>
/// It focuses a <b>window</b>, not a tab. Neither Windows Terminal nor VS Code
/// offers another process a way to select a tab, so a session in the third tab of
/// a terminal brings the terminal forward on whichever tab it last showed.
/// </para>
/// </remarks>
public interface ITerminalFocus
{
    /// <summary>Whether this platform can do either half.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Works out which window hosts the current process's session. Called from the hook process.
    /// </summary>
    /// <returns>The origin, or null when no window could be found. Never throws.</returns>
    SessionOrigin? Capture();

    /// <summary>Brings the origin's window to the front. Called from the app.</summary>
    /// <returns>
    /// True when the window is now in the foreground. False when it no longer
    /// exists, belongs to a different process than was recorded, or the OS refused.
    /// Never throws.
    /// </returns>
    bool TryFocus(SessionOrigin origin);

    /// <summary>Whether the origin's window is the one the user is in right now. Called from the app.</summary>
    /// <returns>
    /// True when the foreground window is the recorded window, or - where the
    /// window is gone or was never known exactly - belongs to the recorded process.
    /// False otherwise, including when the process is no longer the one recorded.
    /// Never throws.
    /// </returns>
    /// <remarks>
    /// Used to hold back a notification about a session the user is looking at:
    /// a command that finishes in a few milliseconds would otherwise announce
    /// itself to someone whose eyes are already on it. Window, not tab - a session
    /// in a background tab of the focused terminal counts as focused.
    /// </remarks>
    bool IsForeground(SessionOrigin origin);
}

/// <summary>The focus helper for a platform that has none yet.</summary>
/// <remarks>
/// macOS needs <c>proc_pidinfo</c> to walk the parents and
/// <c>NSRunningApplication.activate</c> to raise the terminal; Linux has no single
/// answer across X11 and Wayland. Neither can be written or tested from Windows
/// (<c>CLAUDE.md</c> §8), so both are honestly absent. See <c>PLAN.md</c> Phase 9.6.
/// </remarks>
public sealed class NullTerminalFocus : ITerminalFocus
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public SessionOrigin? Capture() => null;

    /// <inheritdoc />
    public bool TryFocus(SessionOrigin origin) => false;

    /// <inheritdoc />
    /// <remarks>Always false, so every notification is shown - what happened before this existed.</remarks>
    public bool IsForeground(SessionOrigin origin) => false;
}
