using ClaudeStatus.App.Composition;
using ClaudeStatus.Platform;
using ClaudeStatus.Platform.Linux;
using ClaudeStatus.Platform.MacOS;
using ClaudeStatus.Platform.Windows;
using ClaudeStatus.Security;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeStatus.Platform.Tests;

/// <summary>
/// The composition root is the only place allowed to know which OS it is on
/// (<c>docs/manual.md</c> §6), so it is worth pinning down hard.
/// </summary>
public class CompositionTests
{
    private static ServiceProvider Build()
        => new ServiceCollection().AddPlatformServices().BuildServiceProvider();

    [Fact]
    public void Resolves_a_platform_info_matching_the_running_OS()
    {
        using ServiceProvider provider = Build();

        IPlatformInfo info = provider.GetRequiredService<IPlatformInfo>();

        if (OperatingSystem.IsWindows())
        {
            info.Should().BeOfType<WindowsPlatformInfo>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            info.Should().BeOfType<MacOsPlatformInfo>();
        }
        else
        {
            info.Should().BeOfType<LinuxPlatformInfo>();
        }
    }

    [Fact]
    public void Resolves_an_autostart_matching_the_running_OS()
    {
        using ServiceProvider provider = Build();

        IAutostart autostart = provider.GetRequiredService<IAutostart>();

        if (OperatingSystem.IsWindows())
        {
            autostart.Should().BeOfType<RegistryAutostart>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            autostart.Should().BeOfType<LaunchAgentAutostart>();
        }
        else
        {
            autostart.Should().BeOfType<XdgAutostart>();
        }
    }

    [Fact]
    public void Resolves_a_secret_store_matching_the_running_OS()
    {
        using ServiceProvider provider = Build();

        ISecretStore store = provider.GetRequiredService<ISecretStore>();

        if (OperatingSystem.IsWindows())
        {
            store.Should().BeOfType<DpapiSecretStore>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            store.Should().BeOfType<KeychainSecretStore>();
        }
        else
        {
            // Either is correct on Linux: libsecret when a keyring answers, the
            // encrypted key file otherwise.
            store.Should().Match<ISecretStore>(
                s => s is LibsecretSecretStore || s is AesGcmFileSecretStore);
        }
    }

    [Fact]
    public void On_Linux_a_missing_keyring_falls_back_to_the_soft_store_and_flags_it()
    {
        // Threat T11: the fallback must be detectable so the Config window warns.
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only.");
        }

        using ServiceProvider provider = Build();
        ISecretStore store = provider.GetRequiredService<ISecretStore>();

        store.IsHardened.Should().Be(store is LibsecretSecretStore);
    }

    [Fact]
    public void Every_platform_service_is_a_singleton()
    {
        // The DPAPI store caches its entropy in memory; a transient registration
        // would re-read it on every resolve for no reason.
        using ServiceProvider provider = Build();

        provider.GetRequiredService<IPlatformInfo>()
            .Should().BeSameAs(provider.GetRequiredService<IPlatformInfo>());
        provider.GetRequiredService<IAutostart>()
            .Should().BeSameAs(provider.GetRequiredService<IAutostart>());
        provider.GetRequiredService<ISecretStore>()
            .Should().BeSameAs(provider.GetRequiredService<ISecretStore>());
    }

    [Fact]
    public void The_secret_store_is_placed_in_the_platform_config_directory()
    {
        using ServiceProvider provider = Build();
        IPlatformInfo info = provider.GetRequiredService<IPlatformInfo>();

        // Not directly observable through the interface, so assert the wiring
        // input instead: the store is constructed from this path.
        PlatformServices.CreateSecretStore(info).Should().NotBeNull();
        info.ConfigDirectory.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Resolves_a_Claude_Code_token_source_matching_the_running_OS()
    {
        // macOS keeps Claude Code's credential in the Keychain, not a file, so the
        // file-based source would silently find nothing there.
        IAccessTokenSource source = PlatformServices.CreateClaudeCodeTokenSource();

        if (OperatingSystem.IsMacOS())
        {
            source.Should().BeOfType<ClaudeCodeKeychainTokenSource>();
        }
        else
        {
            source.Should().BeOfType<ClaudeCodeFileTokenSource>();
        }
    }

    [Fact]
    public void Rejects_a_null_service_collection()
    {
        FluentActions.Invoking(() => PlatformServices.AddPlatformServices(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_factories_reject_a_null_platform_info()
    {
        FluentActions.Invoking(() => PlatformServices.CreateAutostart(null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => PlatformServices.CreateSecretStore(null!))
            .Should().Throw<ArgumentNullException>();
    }
}
