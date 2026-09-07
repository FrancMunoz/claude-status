using System.Runtime.Versioning;
using System.Security;
using ClaudeStatus.Platform;

namespace ClaudeStatus.Platform.MacOS;

/// <summary>
/// Starts at login through a LaunchAgent plist in <c>~/Library/LaunchAgents</c>.
/// </summary>
/// <remarks>
/// <para>
/// Per-user, so no elevation and no <c>sudo</c>. <c>launchd</c> reads the folder
/// at login, so writing the file is enough - we deliberately do not shell out to
/// <c>launchctl load</c>, which would start the app immediately and surprise
/// someone who has just ticked a checkbox.
/// </para>
/// <para>
/// <c>RunAtLoad</c> is true and <c>KeepAlive</c> is absent: this is a tray app
/// the user may legitimately quit, and <c>KeepAlive</c> would relaunch it
/// against their wishes.
/// </para>
/// <para>
/// <b>Not verifiable on the development machine.</b> Exercised on the macOS CI leg.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class LaunchAgentAutostart : IAutostart
{
    /// <summary>Reverse-DNS label, also the plist file name stem.</summary>
    public const string AgentLabel = "com.zeroworks.claudestatus";

    private readonly IPlatformInfo _platform;

    public LaunchAgentAutostart(IPlatformInfo platform)
        => _platform = platform ?? throw new ArgumentNullException(nameof(platform));

    /// <inheritdoc />
    public string DescriptionKey => "Autostart_MacLaunchAgent";

    /// <inheritdoc />
    public bool IsSupported => _platform.ExecutablePath is not null;

    /// <summary>Full path of the plist we manage.</summary>
    public static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "LaunchAgents",
        AgentLabel + ".plist");

    /// <inheritdoc />
    public Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(PlistPath));
    }

    /// <inheritdoc />
    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (!enabled)
        {
            try
            {
                if (File.Exists(PlistPath))
                {
                    File.Delete(PlistPath);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AutostartException(
                    "Autostart_Error_Remove", "Could not remove the LaunchAgent.", ex);
            }
        }

        string executable = _platform.ExecutablePath
            ?? throw new AutostartException(
                "Autostart_Error_PathUnknown",
                "Could not determine the ClaudeStatus executable path, so autostart cannot be enabled.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            await File.WriteAllTextAsync(PlistPath, BuildPlist(executable), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AutostartException(
                "Autostart_Error_Write", "Could not write the LaunchAgent.", ex);
        }
    }

    /// <summary>
    /// Renders the plist.
    /// </summary>
    /// <remarks>
    /// The path is XML-escaped. A macOS path may legally contain <c>&amp;</c> or
    /// <c>&lt;</c>, and an unescaped one produces a plist launchd silently ignores.
    /// </remarks>
    internal static string BuildPlist(string executablePath)
        => $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{AgentLabel}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{SecurityElement.Escape(executablePath)}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>ProcessType</key>
                <string>Interactive</string>
            </dict>
            </plist>

            """;
}
