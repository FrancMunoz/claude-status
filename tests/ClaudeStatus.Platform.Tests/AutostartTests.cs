using ClaudeStatus.Platform;
using ClaudeStatus.Platform.Linux;
using ClaudeStatus.Platform.MacOS;
using ClaudeStatus.Platform.Windows;
using Microsoft.Win32;

namespace ClaudeStatus.Platform.Tests;

/// <summary>
/// Windows autostart, exercised against the real registry.
/// </summary>
/// <remarks>
/// Uses <c>HKCU</c>, so it needs no elevation and touches nothing outside the
/// current user. Every test removes its own value, including on failure.
/// </remarks>
public sealed class RegistryAutostartTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Never leave a Run entry behind on a developer's machine.
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            RegistryAutostart.RunKeyPath, writable: true);
        key?.DeleteValue(RegistryAutostart.ValueName, throwOnMissingValue: false);
    }

    private static RegistryAutostart Create() => new(new StubPlatformInfo());

    [Fact]
    public async Task Starts_out_disabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        (await Create().IsEnabledAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Enabling_then_reading_back_reports_enabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        RegistryAutostart autostart = Create();
        await autostart.SetEnabledAsync(true, Ct);

        (await autostart.IsEnabledAsync(Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Disabling_removes_the_entry()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        RegistryAutostart autostart = Create();
        await autostart.SetEnabledAsync(true, Ct);
        await autostart.SetEnabledAsync(false, Ct);

        (await autostart.IsEnabledAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Disabling_when_already_disabled_is_not_an_error()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        Func<Task> act = () => Create().SetEnabledAsync(false, Ct);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Enabling_twice_is_not_an_error()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        RegistryAutostart autostart = Create();
        await autostart.SetEnabledAsync(true, Ct);

        Func<Task> act = () => autostart.SetEnabledAsync(true, Ct);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_stored_command_quotes_the_path()
    {
        // C:\Program Files\... is the normal install location, and an unquoted
        // path with a space is parsed as a command plus arguments.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        await Create().SetEnabledAsync(true, Ct);

        using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryAutostart.RunKeyPath)!;
        string value = (string)key.GetValue(RegistryAutostart.ValueName)!;

        value.Should().StartWith("\"").And.EndWith("\"");
    }

    [Fact]
    public async Task Refuses_to_enable_when_the_executable_path_is_unknown()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        var autostart = new RegistryAutostart(new StubPlatformInfo { Executable = null });

        autostart.IsSupported.Should().BeFalse();
        await FluentActions.Awaiting(() => autostart.SetEnabledAsync(true, Ct))
            .Should().ThrowAsync<AutostartException>();
    }

    [Fact]
    public void Rejects_a_null_platform_info()
    {
        FluentActions.Invoking(() => new RegistryAutostart(null!))
            .Should().Throw<ArgumentNullException>();
    }
}

/// <summary>
/// The generated content of the macOS and Linux autostart entries.
/// </summary>
/// <remarks>
/// These run on every OS because they are pure string generation. Actually
/// writing the files is covered on the relevant CI leg by
/// <see cref="FileBasedAutostartTests"/>.
/// </remarks>
public class AutostartContentTests
{
    [Fact]
    public void The_plist_names_the_agent_and_runs_at_load()
    {
        string plist = LaunchAgentAutostart.BuildPlist("/Applications/ClaudeStatus.app/Contents/MacOS/ClaudeStatus");

        plist.Should().Contain(LaunchAgentAutostart.AgentLabel);
        plist.Should().Contain("<key>RunAtLoad</key>");
        plist.Should().Contain("/Applications/ClaudeStatus.app/Contents/MacOS/ClaudeStatus");
    }

    [Fact]
    public void The_plist_does_not_set_KeepAlive()
    {
        // This is a tray app the user may legitimately quit. KeepAlive would
        // relaunch it against their wishes.
        LaunchAgentAutostart.BuildPlist("/tmp/app").Should().NotContain("KeepAlive");
    }

    [Fact]
    public void The_plist_escapes_XML_special_characters_in_the_path()
    {
        // A macOS path may legally contain & or <, and an unescaped one produces
        // a plist launchd silently ignores.
        string plist = LaunchAgentAutostart.BuildPlist("/Users/a&b/<app>/ClaudeStatus");

        plist.Should().Contain("&amp;");
        plist.Should().NotContain("/Users/a&b/");
    }

    [Fact]
    public void The_desktop_entry_declares_the_required_keys()
    {
        string entry = XdgAutostart.BuildDesktopEntry("/usr/bin/claudestatus");

        entry.Should().StartWith("[Desktop Entry]");
        entry.Should().Contain("Type=Application");
        entry.Should().Contain("Exec=/usr/bin/claudestatus");
        entry.Should().Contain("Terminal=false");
    }

    [Fact]
    public void The_desktop_entry_quotes_a_path_containing_spaces()
    {
        string entry = XdgAutostart.BuildDesktopEntry("/opt/Claude Status/claudestatus");

        entry.Should().Contain("Exec=\"/opt/Claude Status/claudestatus\"");
    }

    [Fact]
    public void The_desktop_entry_escapes_backslashes()
    {
        // The desktop entry spec makes backslash an escape character inside a
        // string value, so a literal one has to be doubled.
        string entry = XdgAutostart.BuildDesktopEntry("/opt/we\\ird/claudestatus");

        entry.Should().Contain("/opt/we\\\\ird/claudestatus");
    }
}

/// <summary>The macOS and Linux file-writing paths, on their own OS only.</summary>
public class FileBasedAutostartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MacOS_writes_then_removes_the_LaunchAgent()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        var autostart = new LaunchAgentAutostart(new StubPlatformInfo());
        try
        {
            await autostart.SetEnabledAsync(true, Ct);
            (await autostart.IsEnabledAsync(Ct)).Should().BeTrue();

            await autostart.SetEnabledAsync(false, Ct);
            (await autostart.IsEnabledAsync(Ct)).Should().BeFalse();
        }
        finally
        {
            await autostart.SetEnabledAsync(false, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Linux_writes_then_removes_the_desktop_entry()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only.");
        }

        var autostart = new XdgAutostart(new StubPlatformInfo());
        try
        {
            await autostart.SetEnabledAsync(true, Ct);
            (await autostart.IsEnabledAsync(Ct)).Should().BeTrue();

            await autostart.SetEnabledAsync(false, Ct);
            (await autostart.IsEnabledAsync(Ct)).Should().BeFalse();
        }
        finally
        {
            await autostart.SetEnabledAsync(false, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disabling_something_never_enabled_is_not_an_error_on_any_OS()
    {
        IAutostart autostart =
            OperatingSystem.IsWindows() ? new RegistryAutostart(new StubPlatformInfo())
            : OperatingSystem.IsMacOS() ? new LaunchAgentAutostart(new StubPlatformInfo())
            : new XdgAutostart(new StubPlatformInfo());

        Func<Task> act = () => autostart.SetEnabledAsync(false, Ct);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Every_autostart_describes_its_mechanism_for_the_Config_window()
    {
        new RegistryAutostart(new StubPlatformInfo()).DescriptionKey.Should().NotBeNullOrWhiteSpace();
        new LaunchAgentAutostart(new StubPlatformInfo()).DescriptionKey.Should().NotBeNullOrWhiteSpace();
        new XdgAutostart(new StubPlatformInfo()).DescriptionKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Every_autostart_rejects_a_null_platform_info()
    {
        FluentActions.Invoking(() => new RegistryAutostart(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new LaunchAgentAutostart(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new XdgAutostart(null!)).Should().Throw<ArgumentNullException>();
    }
}

/// <summary>A platform info whose executable path is the running test host.</summary>
internal sealed class StubPlatformInfo : IPlatformInfo
{
    public PlatformKind Kind => PlatformKind.Windows;

    public string OperatingSystemName => "Test";

    public string ConfigDirectory => Path.Combine(Path.GetTempPath(), "claudestatus-autostart-tests");

    public TraySupport TraySupport => TraySupport.Available;

    public string? Executable { get; init; } = Environment.ProcessPath;

    public string? ExecutablePath => Executable;

    public bool SupportsInlineTrayText { get; init; }

    public bool TrayIsAtTop { get; init; }
}
