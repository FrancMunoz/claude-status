using System.Runtime.Versioning;
using ClaudeStatus.Platform;
using Microsoft.Win32;

namespace ClaudeStatus.Platform.Windows;

/// <summary>
/// Reads whether the Windows taskbar is light or dark.
/// </summary>
/// <remarks>
/// <para>
/// Windows keeps two separate theme settings. <c>SystemUsesLightTheme</c> is the
/// one that governs the taskbar and notification area; <c>AppsUseLightTheme</c>
/// governs application windows and is <b>not</b> what we want — a user can very
/// reasonably run light apps on a dark taskbar, and reading the wrong key would
/// invert the icon for them.
/// </para>
/// <para>
/// Read fresh on every call so switching the system theme takes effect on the
/// next poll. The value sits in the registry's memory-backed user hive, so this
/// is far cheaper than the once-a-minute call rate makes it look.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrayThemeProvider : ITrayThemeProvider
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string SystemThemeValue = "SystemUsesLightTheme";

    /// <inheritdoc />
    public TrayBackground Current
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                object? value = key?.GetValue(SystemThemeValue);

                return value switch
                {
                    int usesLight => usesLight != 0 ? TrayBackground.Light : TrayBackground.Dark,

                    // The key is absent on some older or policy-managed installs.
                    // Dark has been the taskbar default since Windows 10, but
                    // "Unknown" makes the renderer outline the glyph, which is
                    // correct either way. Guessing would be worse.
                    _ => TrayBackground.Unknown,
                };
            }
            catch (Exception ex) when (ex is System.Security.SecurityException
                or UnauthorizedAccessException or IOException)
            {
                return TrayBackground.Unknown;
            }
        }
    }
}
