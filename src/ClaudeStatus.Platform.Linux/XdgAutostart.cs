using System.Runtime.Versioning;
using ClaudeStatus.Platform;

namespace ClaudeStatus.Platform.Linux;

/// <summary>
/// Starts at login through an XDG autostart <c>.desktop</c> entry.
/// </summary>
/// <remarks>
/// <para>
/// Writes <c>$XDG_CONFIG_HOME/autostart/claudestatus.desktop</c> (or
/// <c>~/.config/autostart/…</c>), which every mainstream desktop reads.
/// </para>
/// <para>
/// <b>Not verifiable on the development machine.</b> Exercised on the Linux CI leg.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class XdgAutostart : IAutostart
{
    /// <summary>The desktop entry file name.</summary>
    public const string DesktopFileName = "claudestatus.desktop";

    private readonly IPlatformInfo _platform;

    public XdgAutostart(IPlatformInfo platform)
        => _platform = platform ?? throw new ArgumentNullException(nameof(platform));

    /// <inheritdoc />
    public string DescriptionKey => "Autostart_LinuxXdg";

    /// <inheritdoc />
    public bool IsSupported => _platform.ExecutablePath is not null;

    /// <summary>Full path of the desktop entry we manage.</summary>
    public static string DesktopFilePath
    {
        get
        {
            string? xdg = Environment.GetEnvironmentVariable(LinuxPlatformInfo.XdgConfigHome);
            string root = !string.IsNullOrWhiteSpace(xdg)
                ? xdg
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

            return Path.Combine(root, "autostart", DesktopFileName);
        }
    }

    /// <inheritdoc />
    public Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(DesktopFilePath));
    }

    /// <inheritdoc />
    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (!enabled)
        {
            try
            {
                if (File.Exists(DesktopFilePath))
                {
                    File.Delete(DesktopFilePath);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AutostartException(
                    "Autostart_Error_Remove", "Could not remove the autostart entry.", ex);
            }
        }

        string executable = _platform.ExecutablePath
            ?? throw new AutostartException(
                "Autostart_Error_PathUnknown",
                "Could not determine the ClaudeStatus executable path, so autostart cannot be enabled.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFilePath)!);
            await File.WriteAllTextAsync(DesktopFilePath, BuildDesktopEntry(executable), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AutostartException(
                "Autostart_Error_Write", "Could not write the autostart entry.", ex);
        }
    }

    /// <summary>
    /// Renders the desktop entry.
    /// </summary>
    /// <remarks>
    /// The desktop entry spec makes backslash an escape character inside a string
    /// value, and requires a path containing spaces to be quoted in <c>Exec</c>.
    /// Both are handled here rather than trusting the path to be tidy.
    /// </remarks>
    internal static string BuildDesktopEntry(string executablePath)
    {
        string escaped = executablePath.Replace("\\", "\\\\", StringComparison.Ordinal);
        string exec = escaped.Contains(' ', StringComparison.Ordinal)
            ? "\"" + escaped.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : escaped;

        return $"""
            [Desktop Entry]
            Type=Application
            Name=ClaudeStatus
            Comment=Claude subscription usage in the system tray
            Exec={exec}
            Terminal=false
            Categories=Utility;
            X-GNOME-Autostart-enabled=true

            """;
    }
}
