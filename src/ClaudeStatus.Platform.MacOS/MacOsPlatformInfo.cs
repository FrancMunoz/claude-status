using System.Runtime.Versioning;
using ClaudeStatus.Platform;

namespace ClaudeStatus.Platform.MacOS;

/// <summary>macOS paths and capabilities.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsPlatformInfo : PlatformInfoBase
{
    /// <inheritdoc />
    public override PlatformKind Kind => PlatformKind.MacOS;

    /// <inheritdoc />
    /// <remarks>
    /// <c>~/Library/Application Support/ClaudeStatus</c>, the documented home for
    /// per-user application data. <see cref="Environment.SpecialFolder.ApplicationData"/>
    /// maps to <c>~/.config</c> on .NET for macOS, which is not where a mac user
    /// expects to find it, so the path is built explicitly.
    /// </remarks>
    public override string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "Application Support",
        ApplicationFolderName);

    /// <inheritdoc />
    /// <remarks>The menu bar is always there.</remarks>
    public override TraySupport TraySupport => TraySupport.Available;
}
