using Avalonia.Controls;

namespace ClaudeStatus.App.Views;

/// <summary>
/// Makes a window hide when the user closes it, and really close when the app does.
/// </summary>
/// <remarks>
/// <para>
/// Config, the report and Info are built once and reused, so closing one hides it.
/// That used to be an unconditional <c>Cancel</c> in each window's <c>Closing</c>
/// handler, which also refused the close Avalonia asks for while the app shuts down.
/// </para>
/// <para>
/// A refusal there cancels the whole shutdown. On macOS a quit from outside the menu -
/// an Apple Event, logout, restart - goes through that non-forced path: the quit was
/// answered with <c>NSTerminateCancel</c> (the <c>-128</c> a scripted quit got back),
/// and the app, already torn down by then, kept running as an empty process. Windows'
/// logout and shutdown take the same path in Avalonia. So a close for any shutdown
/// reason goes through, on every platform; everything else still hides.
/// </para>
/// </remarks>
internal static class HideOnClose
{
    /// <summary>Hides <paramref name="window"/> instead of closing it, except during shutdown.</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.Closing += (_, args) =>
        {
            if (ShouldHide(args.CloseReason))
            {
                args.Cancel = true;
                window.Hide();
            }
        };
    }

    /// <summary>
    /// Whether a close for this reason is the user dismissing the window rather than the
    /// app or the OS ending.
    /// </summary>
    /// <remarks>
    /// Only the two shutdown reasons close. An <c>Undefined</c> reason - a platform that
    /// did not say - is treated as the user, which is what it was before.
    /// </remarks>
    internal static bool ShouldHide(WindowCloseReason reason)
        => reason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown);
}
