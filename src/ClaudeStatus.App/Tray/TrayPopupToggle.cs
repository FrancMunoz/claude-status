namespace ClaudeStatus.App.Tray;

/// <summary>What a left click on the tray icon should do to the details popup.</summary>
internal enum PopupClickResult
{
    /// <summary>Open it.</summary>
    Show,

    /// <summary>It is open and focused; close it.</summary>
    Hide,

    /// <summary>Do nothing - this click is what closed it a moment ago.</summary>
    Ignore,
}

/// <summary>
/// Decides whether a tray click opens or closes the details popup.
/// </summary>
/// <remarks>
/// <para>
/// This looks like it should be a one-line <c>IsVisible ? Hide : Show</c>, and it
/// cannot be. The popup hides itself when it loses focus, and clicking the tray
/// icon takes focus away from it - so by the time the click handler runs, the
/// window has <b>already</b> hidden and <c>IsVisible</c> is false. A naive toggle
/// therefore reopens the window the user just asked to close, and the icon
/// appears not to respond at all.
/// </para>
/// <para>
/// The fix is to notice that the click arrived immediately after the dismissal
/// and treat the two as one gesture. Pure and separated from the controller so
/// the timing rule can be tested without a window, a tray, or a real clock.
/// </para>
/// </remarks>
internal static class TrayPopupToggle
{
    /// <summary>
    /// How long after an auto-dismissal a click still counts as "that click".
    /// </summary>
    /// <remarks>
    /// The real gap between the deactivation and the click reaching us is a few
    /// milliseconds - they are two halves of one mouse press. A quarter of a second
    /// covers that with room to spare while still being far shorter than a
    /// deliberate second click, so reopening never feels blocked.
    /// </remarks>
    public static readonly TimeSpan ReopenSuppression = TimeSpan.FromMilliseconds(250);

    /// <param name="isVisible">Whether the popup is on screen right now.</param>
    /// <param name="sinceDismissed">
    /// How long ago the popup last hid itself through losing focus.
    /// </param>
    public static PopupClickResult Decide(bool isVisible, TimeSpan sinceDismissed)
        => Decide(isVisible, sinceDismissed, ReopenSuppression);

    /// <summary>The rule, with an explicit window so tests do not depend on the constant.</summary>
    public static PopupClickResult Decide(
        bool isVisible, TimeSpan sinceDismissed, TimeSpan suppression)
    {
        if (isVisible)
        {
            // Reachable on backends that do not deactivate the window first, and
            // whenever the popup is open but not focused.
            return PopupClickResult.Hide;
        }

        // A negative age means the clock went backwards - a resume from sleep, or
        // an NTP correction. Treat that as "long ago" rather than as "just now":
        // refusing to open the window is a worse failure than opening it twice.
        return sinceDismissed >= TimeSpan.Zero && sinceDismissed < suppression
            ? PopupClickResult.Ignore
            : PopupClickResult.Show;
    }
}
