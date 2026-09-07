namespace ClaudeStatus.Platform;

/// <summary>Which OS family the app is running on.</summary>
public enum PlatformKind
{
    /// <summary>Not one of the three supported families.</summary>
    Unknown = 0,

    /// <summary>Windows 10 or 11.</summary>
    Windows = 1,

    /// <summary>macOS 13 or later.</summary>
    MacOS = 2,

    /// <summary>A Linux desktop.</summary>
    Linux = 3,
}

/// <summary>How confident we are that a system tray exists.</summary>
public enum TraySupport
{
    /// <summary>A tray is available.</summary>
    Available = 0,

    /// <summary>
    /// There is probably no tray - a bare GNOME session without the AppIndicator
    /// extension, for instance.
    /// </summary>
    /// <remarks>
    /// A guess, not a fact. Avalonia's <c>TrayIcon</c> gives no reliable feedback,
    /// so the app still tries and warns rather than refusing to start.
    /// </remarks>
    Unlikely = 1,

    /// <summary>Headless: no display server at all.</summary>
    Unavailable = 2,
}

/// <summary>Everything the app needs to know about where it is running.</summary>
public interface IPlatformInfo
{
    /// <summary>The OS family.</summary>
    PlatformKind Kind { get; }

    /// <summary>A human-readable OS name and version for the Info window.</summary>
    string OperatingSystemName { get; }

    /// <summary>
    /// Where settings and logs live. Created on demand; never contains secret
    /// bytes (see <c>docs/security.md</c> §4).
    /// </summary>
    string ConfigDirectory { get; }

    /// <summary>Whether a system tray is likely to work here.</summary>
    TraySupport TraySupport { get; }

    /// <summary>
    /// The path to use when registering autostart, or null when it cannot be
    /// determined.
    /// </summary>
    string? ExecutablePath { get; }

    /// <summary>
    /// Whether the indicator may be wider than it is tall, so several metrics can
    /// be written across it.
    /// </summary>
    /// <remarks>
    /// True on macOS, where a menu bar item sizes itself to whatever image it is
    /// given. A Windows notification-area icon is a fixed square and a Linux panel
    /// makes no promise either way, so both get the single-number icon and
    /// <see cref="ClaudeStatus.Usage.IndicatorMode.Row"/> falls back.
    /// </remarks>
    bool SupportsInlineTrayText { get; }

    /// <summary>Whether the indicator lives along the top edge of the screen.</summary>
    /// <remarks>
    /// A fact on macOS, where the menu bar cannot be moved, and the reason the
    /// popup needs to be told rather than left to infer it: the inference reads the
    /// screen's insets, and a Dock along the bottom is deeper than the menu bar, so
    /// it concluded "tray at the bottom" and put the popup in the wrong corner.
    /// </remarks>
    bool TrayIsAtTop { get; }
}
