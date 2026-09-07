using Avalonia;

namespace ClaudeStatus.App.Tray;

/// <summary>
/// Works out where a tray popup should sit on screen.
/// </summary>
/// <remarks>
/// <para>
/// Pure geometry, deliberately separated from <c>TrayApplicationController</c> so
/// it can be tested without a window. Everything here is in <b>physical pixels</b>,
/// because that is the unit <see cref="Avalonia.Platform.Screen.WorkingArea"/> and
/// <see cref="Avalonia.Controls.WindowBase.Position"/> both use. Window sizes, by
/// contrast, are in device-independent units - mixing the two was the original bug:
/// on a 150 % display a 340-unit-wide window was treated as 340 px, so anchoring it
/// to the right edge left roughly a third of it hanging off the screen.
/// </para>
/// <para>
/// Avalonia exposes no tray-icon position on any backend, so the anchor corner is
/// inferred from where the taskbar is - the gap between the screen bounds and its
/// working area. That puts the popup under a bottom taskbar on Windows, under the
/// menu bar on macOS, and beside a left- or right-docked panel on Linux.
/// </para>
/// </remarks>
internal static class TrayPopupPlacement
{
    /// <summary>Gap between the popup and the screen edges, in device-independent units.</summary>
    private const double MarginDip = 12;

    /// <summary>
    /// Places a popup of <paramref name="sizeDip"/> near the tray on a screen.
    /// </summary>
    /// <param name="screenBounds">The full screen rectangle, in physical pixels.</param>
    /// <param name="workingArea">The screen minus taskbars and docks, in physical pixels.</param>
    /// <param name="sizeDip">The popup size in device-independent units.</param>
    /// <param name="scaling">Physical pixels per device-independent unit.</param>
    /// <returns>
    /// The top-left corner in physical pixels, always inside <paramref name="workingArea"/>
    /// unless the popup is larger than the working area itself, in which case it is
    /// pinned to the top-left so at least the beginning of the content is reachable.
    /// </returns>
    public static PixelPoint Place(
        PixelRect screenBounds, PixelRect workingArea, Size sizeDip, double scaling)
    {
        // A non-finite or absurd scaling would silently produce a window parked in
        // another timezone. Anything outside this range is a broken backend, not a
        // display the user owns.
        if (double.IsNaN(scaling) || scaling < 0.25 || scaling > 8)
        {
            scaling = 1;
        }

        int width = ToPixels(sizeDip.Width, scaling, fallbackDip: 340);
        int height = ToPixels(sizeDip.Height, scaling, fallbackDip: 320);
        int margin = (int)Math.Round(MarginDip * scaling);

        // Which edge is the taskbar on? The working area is inset from the bounds on
        // exactly that side. Ties (or no taskbar at all) fall through to bottom-right,
        // which is where the notification area lives on a default Windows install.
        int insetTop = workingArea.Y - screenBounds.Y;
        int insetBottom = screenBounds.Bottom - workingArea.Bottom;
        int insetLeft = workingArea.X - screenBounds.X;
        int insetRight = screenBounds.Right - workingArea.Right;

        bool atTop = insetTop > insetBottom && insetTop >= insetLeft && insetTop >= insetRight;
        bool atLeft = insetLeft > insetRight && insetLeft >= insetTop && insetLeft >= insetBottom;

        int x = atLeft ? workingArea.X + margin : workingArea.Right - width - margin;
        int y = atTop ? workingArea.Y + margin : workingArea.Bottom - height - margin;

        // The clamp is what actually guarantees the window is on screen. The anchor
        // above is a preference; this is the rule. It also copes with the popup being
        // taller than the working area, which a small laptop screen can produce.
        return new PixelPoint(
            Clamp(x, workingArea.X, workingArea.Right - width),
            Clamp(y, workingArea.Y, workingArea.Bottom - height));
    }

    /// <summary>Converts a device-independent length to physical pixels.</summary>
    /// <remarks>
    /// A window that has never been laid out reports a size of zero or NaN. Guessing
    /// is unavoidable there, but the guess only has to survive until the first layout
    /// pass, after which the caller repositions with the measured size.
    /// </remarks>
    private static int ToPixels(double dip, double scaling, double fallbackDip)
    {
        double value = double.IsNaN(dip) || dip <= 0 ? fallbackDip : dip;
        return (int)Math.Ceiling(value * scaling);
    }

    /// <summary>Clamps, tolerating an inverted range instead of throwing.</summary>
    /// <remarks>
    /// <see cref="Math.Clamp(int, int, int)"/> throws when <c>max &lt; min</c>, which
    /// happens whenever the popup is wider than the working area. Pinning to the
    /// minimum is the useful answer there.
    /// </remarks>
    private static int Clamp(int value, int min, int max)
        => max <= min ? min : Math.Clamp(value, min, max);
}
