using System.Runtime.Versioning;
using ClaudeStatus.Platform;

namespace ClaudeStatus.Platform.Windows;

/// <summary>Windows paths and capabilities.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformInfo : PlatformInfoBase
{
    /// <inheritdoc />
    public override PlatformKind Kind => PlatformKind.Windows;

    /// <inheritdoc />
    /// <remarks><c>%APPDATA%\ClaudeStatus</c> - roams with the user profile, which is what we want.</remarks>
    public override string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ApplicationFolderName);

    /// <inheritdoc />
    /// <remarks>The notification area is always present on Windows 10 and 11.</remarks>
    public override TraySupport TraySupport => TraySupport.Available;
}
