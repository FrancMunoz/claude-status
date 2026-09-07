using System.Runtime.Versioning;
using ClaudeStatus.Platform;

namespace ClaudeStatus.Platform.Linux;

/// <summary>Linux paths and capabilities.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformInfo : PlatformInfoBase
{
    /// <summary>The XDG variable that relocates per-user config.</summary>
    public const string XdgConfigHome = "XDG_CONFIG_HOME";

    private const string DirectoryName = "claudestatus";

    /// <inheritdoc />
    public override PlatformKind Kind => PlatformKind.Linux;

    /// <inheritdoc />
    /// <remarks>
    /// <c>$XDG_CONFIG_HOME/claudestatus</c>, falling back to
    /// <c>~/.config/claudestatus</c>. Lower-case, as is the convention on Linux.
    /// </remarks>
    public override string ConfigDirectory
    {
        get
        {
            string? xdg = Environment.GetEnvironmentVariable(XdgConfigHome);
            string root = !string.IsNullOrWhiteSpace(xdg)
                ? xdg
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

            return Path.Combine(root, DirectoryName);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A guess, and deliberately a soft one. There is no reliable way to ask
    /// "is there a tray?" - StatusNotifierItem support depends on the desktop, the
    /// version and, on GNOME, on a third-party extension.
    /// </para>
    /// <para>
    /// No display server at all is the one case we can be certain about. A bare
    /// GNOME session is reported as <see cref="TraySupport.Unlikely"/> so the app
    /// can warn, but it still starts and still tries.
    /// </para>
    /// </remarks>
    public override TraySupport TraySupport
    {
        get
        {
            bool hasDisplay = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

            if (!hasDisplay)
            {
                return TraySupport.Unavailable;
            }

            string desktop = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty)
                .ToLowerInvariant();

            // KDE, XFCE, Cinnamon, MATE and Budgie all ship a tray. GNOME needs
            // an extension, which we cannot detect from here.
            if (desktop.Contains("gnome", StringComparison.Ordinal)
                && !desktop.Contains("unity", StringComparison.Ordinal))
            {
                return TraySupport.Unlikely;
            }

            return TraySupport.Available;
        }
    }
}
