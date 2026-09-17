namespace ClaudeStatus.Platform;

/// <summary>
/// Raises a notification the operating system owns.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="IStatusIndicator.ShowNotice"/>, and the distinction
/// is the point. A notice is a card this app draws: it appears beside the
/// indicator, it goes away on a timer, and it leaves no trace. That is right for
/// something the user is already looking at, and wrong for telling somebody who
/// is in another window that a session has finished - which is what this is for.
/// A real notification is queued by the OS, survives being missed, and is found
/// again in the notification centre.
/// </para>
/// <para>
/// Per-OS behind an interface, like everything else platform-shaped
/// (<c>CLAUDE.md</c> §3). Where a platform has nothing to offer, the no-op
/// implementation reports <see cref="IsSupported"/> as false and the caller falls
/// back to the card.
/// </para>
/// </remarks>
public interface INotifier : IDisposable
{
    /// <summary>Whether this platform can actually raise one.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Shows a notification.
    /// </summary>
    /// <param name="title">The heading. Already localised by the caller.</param>
    /// <param name="message">The body. Already localised by the caller.</param>
    /// <param name="tag">
    /// Handed back in <see cref="Activated"/> when this notification is clicked.
    /// Opaque to the notifier; the caller uses a session id.
    /// </param>
    /// <returns>True when the OS accepted it.</returns>
    /// <remarks>
    /// Never throws. This is called from the end of somebody's turn, and a
    /// notification that cannot be shown is not worth an exception travelling up
    /// through the poll loop.
    /// </remarks>
    bool Notify(string title, string message, string? tag = null);

    /// <summary>
    /// Raised when the user clicks a notification this notifier showed.
    /// </summary>
    /// <remarks>
    /// Raised synchronously while the OS still considers the click the latest
    /// input, but not necessarily on the UI thread: the shell icon raises it on the
    /// thread that owns the notifier, a WinRT toast on a thread-pool thread. A
    /// handler that wants to put a window in the foreground must do it before
    /// returning - marshalling with a blocking invoke, never a post - or the OS has
    /// withdrawn the permission and the window only flashes.
    /// </remarks>
    event EventHandler<NotificationActivatedEventArgs>? Activated;
}

/// <summary>A notification was clicked.</summary>
/// <param name="tag">The tag it was shown with.</param>
public sealed class NotificationActivatedEventArgs(string? tag) : EventArgs
{
    /// <summary>The tag passed to <see cref="INotifier.Notify"/>.</summary>
    public string? Tag { get; } = tag;
}

/// <summary>The notifier for a platform that has none yet.</summary>
/// <remarks>
/// macOS and Linux both have a native path - <c>UNUserNotification</c> and
/// <c>org.freedesktop.Notifications</c> - and neither can be written or tested
/// from a Windows machine, so they are honestly absent rather than half done.
/// The caller shows its own card there, which is what every platform did before
/// any of this existed.
/// </remarks>
public sealed class NullNotifier : INotifier
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public bool Notify(string title, string message, string? tag = null) => false;

    /// <inheritdoc />
    /// <remarks>Never raised: nothing is ever shown to be clicked.</remarks>
    public event EventHandler<NotificationActivatedEventArgs>? Activated
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
