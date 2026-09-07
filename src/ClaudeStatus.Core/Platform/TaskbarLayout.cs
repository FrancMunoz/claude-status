namespace ClaudeStatus.Platform;

/// <summary>
/// Decides where the taskbar widget sits.
/// </summary>
/// <remarks>
/// <para>
/// One rule: right-aligned against the system tray, full taskbar height. Anchoring
/// to the tray rather than to the running-apps strip is what makes the same
/// arithmetic work on Windows 10 and 11 and whether the taskbar is centred or
/// left-aligned - the tray is at the right end in every one of those layouts,
/// and it is the one thing Explorer never moves without also moving the clock.
/// </para>
/// <para>
/// Pure so it can be tested. The platform measures; this decides; the platform
/// moves.
/// </para>
/// </remarks>
public static class TaskbarLayout
{
    /// <summary>
    /// Room left for the clock when the tray cannot be measured, in 96-DPI pixels.
    /// </summary>
    /// <remarks>
    /// TrafficMonitor's figure for a Windows 11 secondary taskbar, which shows a
    /// clock but no tray window to measure. Scaled by the taskbar's DPI.
    /// </remarks>
    public const int FallbackTrayWidth = 88;

    /// <summary>Space between the widget and the tray, in 96-DPI pixels.</summary>
    public const int Gap = 4;

    /// <summary>
    /// Computes the slot for a widget of the given width.
    /// </summary>
    /// <param name="metrics">The taskbar as measured right now.</param>
    /// <param name="widthPx">The widget's width in physical pixels.</param>
    /// <returns>
    /// Where to put it, or null when the taskbar is one the widget cannot live in:
    /// vertical, degenerate, or too narrow to hold the widget at all.
    /// </returns>
    public static TaskbarSlot? Compute(TaskbarMetrics metrics, int widthPx)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        PixelBox bar = metrics.Taskbar;
        if (bar.IsEmpty || !metrics.IsHorizontal || widthPx <= 0)
        {
            return null;
        }

        double scale = Math.Max(metrics.Dpi, 96) / 96d;
        int gap = (int)Math.Round(Gap * scale);

        // The right edge the widget must stay left of, relative to the taskbar.
        int limit = metrics.NotifyArea is { IsEmpty: false } tray
            ? tray.Left - bar.Left
            : bar.Width - (int)Math.Round(FallbackTrayWidth * scale);

        int x = limit - gap - widthPx;
        if (x < 0)
        {
            // Nowhere to put it without covering the Start button or running
            // off the left edge. Better to show nothing than something clipped.
            return null;
        }

        return new TaskbarSlot(x, 0, widthPx, bar.Height);
    }
}
