using System.Runtime.Versioning;
using ClaudeStatus.Platform;

namespace ClaudeStatus.Platform.Windows;

/// <summary>
/// Picks the best notifier this Windows build and machine can offer.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsNotifier
{
    /// <summary>
    /// A WinRT toast where the app is installed; the shell icon's balloon otherwise.
    /// </summary>
    /// <remarks>
    /// The toast needs no icon at all, but it needs the installer's Start Menu
    /// shortcut (<see cref="WindowsToastNotifier"/>), so an unzipped copy or a
    /// development build on a machine without the install falls back to
    /// <see cref="ShellNotifyIconNotifier"/>, which shows its icon only while a
    /// notification is up. A build made off Windows has no toast code at all.
    /// </remarks>
    public static INotifier Create()
    {
#if WINDOWS10_0_17763_0_OR_GREATER
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            var toast = new WindowsToastNotifier();
            if (toast.IsSupported)
            {
                return toast;
            }

            toast.Dispose();
        }
#endif

        return new ShellNotifyIconNotifier();
    }
}
