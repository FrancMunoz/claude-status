namespace ClaudeStatus.Platform;

/// <summary>
/// How the application presents itself outside its own windows.
/// </summary>
/// <remarks>
/// <para>
/// This app has no main window and spends its life in the tray or the menu bar, so
/// an entry in the Dock or the task switcher is an offer it cannot honour: clicking
/// it has nothing to bring to the front, and quitting through it bypasses the menu
/// the app actually provides.
/// </para>
/// <para>
/// An interface rather than a call in the composition root because the answer is
/// per-platform and only macOS has one to give. Windows already keeps the app out
/// of the taskbar through <c>ShowInTaskbar</c> on each window, and Linux panels
/// vary too much to promise anything.
/// </para>
/// </remarks>
public interface IAppPresentation
{
    /// <summary>
    /// Asks the OS to treat this as a background application.
    /// </summary>
    /// <remarks>
    /// Best effort and silent on platforms with no such concept. Must be called on
    /// the UI thread, once the windowing system is up.
    /// </remarks>
    /// <returns>Whether the platform did anything.</returns>
    bool HideFromDock();
}

/// <summary>The presentation for platforms with nothing to change.</summary>
public sealed class UnchangedAppPresentation : IAppPresentation
{
    /// <inheritdoc />
    public bool HideFromDock() => false;
}
