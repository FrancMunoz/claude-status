namespace ClaudeStatus.Platform;

/// <summary>
/// Reports where the pointer is, so a popup can be put under the indicator that
/// was just clicked.
/// </summary>
/// <remarks>
/// <para>
/// This exists because no backend tells us where the tray icon is. Avalonia's
/// <c>TrayIcon</c> exposes no position on any platform, and macOS deliberately
/// keeps <c>NSStatusItem</c> placement to itself - it shifts as other apps add and
/// remove menu bar items, so there is nothing stable to ask for even natively.
/// </para>
/// <para>
/// The pointer is the way round it. A tray popup only ever opens because the user
/// clicked the icon, and at that instant the pointer is on the icon - so the
/// pointer's x is the icon's x, to within half an icon. It costs one syscall and
/// needs no private API.
/// </para>
/// <para>
/// It is a position, not a promise: the value is only meaningful when read during
/// the click that opened the popup. Read at any other time it reports wherever the
/// mouse happens to be, which is why the caller captures it on the click rather
/// than when placing the window.
/// </para>
/// </remarks>
public interface ITrayPointerLocator
{
    /// <summary>
    /// The pointer's horizontal position, or null where it cannot be read.
    /// </summary>
    /// <remarks>
    /// In the screen units the OS itself uses - points on macOS, which is what
    /// <c>Screen.Scaling</c> converts from, not physical pixels. Null means "place
    /// the popup the way you would have anyway"; it is never an error worth
    /// surfacing, because a popup in the corner is a perfectly good fallback.
    /// </remarks>
    double? PointerX { get; }
}

/// <summary>
/// The locator for platforms with no way to ask, or no need to.
/// </summary>
/// <remarks>
/// Windows and Linux both anchor the popup to a screen corner, which needs no
/// pointer at all, so reporting null here costs them nothing.
/// </remarks>
public sealed class UnknownTrayPointerLocator : ITrayPointerLocator
{
    /// <inheritdoc />
    public double? PointerX => null;
}
