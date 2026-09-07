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
}
