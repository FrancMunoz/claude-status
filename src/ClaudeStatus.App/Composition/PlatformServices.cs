using ClaudeStatus.Platform;
using ClaudeStatus.Platform.Linux;
using ClaudeStatus.Platform.MacOS;
using ClaudeStatus.Platform.Windows;
using ClaudeStatus.Security;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeStatus.App.Composition;

/// <summary>
/// The one place in the codebase that knows which OS it is running on.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/manual.md</c> §6: UI and Core never branch on the operating system. Every
/// <c>#if</c>-shaped decision is concentrated here, resolved once at startup, and
/// everything downstream sees only interfaces.
/// </para>
/// <para>
/// Deliberately free of Avalonia types so it can be unit tested without a UI.
/// </para>
/// </remarks>
public static class PlatformServices
{
    /// <summary>Registers the platform implementations for the running OS.</summary>
    /// <exception cref="PlatformNotSupportedException">Not Windows, macOS, or Linux.</exception>
    public static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(_ => CreatePlatformInfo());
        services.AddSingleton(provider => CreateAutostart(provider.GetRequiredService<IPlatformInfo>()));
        services.AddSingleton(provider => CreateSecretStore(provider.GetRequiredService<IPlatformInfo>()));
        services.AddSingleton(_ => CreateTrayThemeProvider());
        services.AddSingleton(_ => CreateTaskbarHost());

        return services;
    }

    /// <summary>
    /// Builds the taskbar host for the running OS.
    /// </summary>
    /// <remarks>
    /// Only Windows has a taskbar a window can be re-parented into. The others
    /// get an inert host, and the app keeps using the tray icon there.
    /// </remarks>
    public static ITaskbarHost CreateTaskbarHost()
        => OperatingSystem.IsWindows()
            ? new WindowsTaskbarHost()
            : new UnsupportedTaskbarHost();

    /// <summary>Builds the <see cref="IPlatformInfo"/> for the running OS.</summary>
    public static IPlatformInfo CreatePlatformInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPlatformInfo();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsPlatformInfo();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxPlatformInfo();
        }

        throw new PlatformNotSupportedException(
            "ClaudeStatus supports Windows, macOS and Linux.");
    }

    /// <summary>Builds the <see cref="IAutostart"/> for the running OS.</summary>
    public static IAutostart CreateAutostart(IPlatformInfo platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        if (OperatingSystem.IsWindows())
        {
            return new RegistryAutostart(platform);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new LaunchAgentAutostart(platform);
        }

        if (OperatingSystem.IsLinux())
        {
            return new XdgAutostart(platform);
        }

        throw new PlatformNotSupportedException(
            "ClaudeStatus supports Windows, macOS and Linux.");
    }

    /// <summary>
    /// Builds the <see cref="ISecretStore"/> for the running OS.
    /// </summary>
    /// <remarks>
    /// Linux is the only branch with a choice to make: prefer the system keyring,
    /// and fall back to the encrypted key file when no Secret Service answers.
    /// The fallback reports <see cref="ISecretStore.IsHardened"/> as false, which
    /// is what makes the Config window show its warning
    /// (<c>docs/security.md</c> threat T11).
    /// </remarks>
    public static ISecretStore CreateSecretStore(IPlatformInfo platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        if (OperatingSystem.IsWindows())
        {
            return new DpapiSecretStore(platform.ConfigDirectory);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new KeychainSecretStore();
        }

        if (OperatingSystem.IsLinux())
        {
            return LibsecretSecretStore.IsAvailable()
                ? new LibsecretSecretStore()
                : new AesGcmFileSecretStore(platform.ConfigDirectory);
        }

        throw new PlatformNotSupportedException(
            "ClaudeStatus supports Windows, macOS and Linux.");
    }

    /// <summary>
    /// Builds the tray theme provider for the running OS.
    /// </summary>
    /// <remarks>
    /// Only Windows exposes a usable signal. macOS needs none - a template icon is
    /// recoloured for the menu bar automatically. Linux has no portable way to ask
    /// the panel what colour it is, so it reports Unknown and the renderer draws a
    /// halo that reads either way.
    /// </remarks>
    public static ITrayThemeProvider CreateTrayThemeProvider()
        => OperatingSystem.IsWindows()
            ? new WindowsTrayThemeProvider()
            : new StaticTrayThemeProvider(TrayBackground.Unknown);

    /// <summary>
    /// Builds the token source that reads Claude Code's existing login.
    /// </summary>
    /// <remarks>
    /// Windows and Linux keep the credential in a file. macOS keeps it in the
    /// Keychain under the item name Claude Code itself uses, so there is no file
    /// to read - see <c>docs/data-source.md</c>.
    /// </remarks>
    public static IAccessTokenSource CreateClaudeCodeTokenSource(TimeProvider? clock = null)
        => OperatingSystem.IsMacOS()
            ? new ClaudeCodeKeychainTokenSource(clock)
            : new ClaudeCodeFileTokenSource(clock: clock);
}
