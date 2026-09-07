using ClaudeStatus.Platform;
using ClaudeStatus.Platform.Linux;
using ClaudeStatus.Platform.MacOS;
using ClaudeStatus.Platform.Windows;

namespace ClaudeStatus.Platform.Tests;

/// <summary>
/// Checks that hold on whichever OS the suite happens to run on.
/// </summary>
public class PlatformInfoTests
{
    private static IPlatformInfo Current()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPlatformInfo();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsPlatformInfo();
        }

        return new LinuxPlatformInfo();
    }

    [Fact]
    public void Reports_the_kind_matching_the_running_OS()
    {
        PlatformKind expected =
            OperatingSystem.IsWindows() ? PlatformKind.Windows
            : OperatingSystem.IsMacOS() ? PlatformKind.MacOS
            : PlatformKind.Linux;

        Current().Kind.Should().Be(expected);
    }

    [Fact]
    public void Gives_an_absolute_rooted_config_directory()
    {
        string directory = Current().ConfigDirectory;

        directory.Should().NotBeNullOrWhiteSpace();
        Path.IsPathRooted(directory).Should().BeTrue();
    }

    [Fact]
    public void The_config_directory_is_under_the_user_profile_not_a_shared_location()
    {
        // Settings and logs are per-user. A shared path would leak one user's
        // configuration to another account on the same machine.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string directory = Current().ConfigDirectory;

        directory.Should().StartWith(home);
    }

    [Fact]
    public void Names_the_operating_system_for_the_Info_window()
    {
        Current().OperatingSystemName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Resolves_an_executable_path_that_actually_exists()
    {
        // An autostart entry pointing at a path that does not exist is worse than
        // no autostart entry, so this must resolve to something real or to null.
        string? path = Current().ExecutablePath;

        if (path is not null)
        {
            File.Exists(path).Should().BeTrue();
        }
    }

    [Fact]
    public void EnsureConfigDirectory_creates_it_and_is_idempotent()
    {
        // Against a throwaway directory, never the real one. An earlier version of
        // this test created %APPDATA%ClaudeStatus on the developer's machine just
        // by running the suite, which is not something a test should do.
        var platform = new TempPlatformInfo();
        try
        {
            string created = platform.EnsureConfigDirectory();
            string again = platform.EnsureConfigDirectory();

            Directory.Exists(created).Should().BeTrue();
            again.Should().Be(created);
        }
        finally
        {
            if (Directory.Exists(platform.ConfigDirectory))
            {
                Directory.Delete(platform.ConfigDirectory, recursive: true);
            }
        }
    }

    /// <summary>A platform info rooted in a throwaway directory.</summary>
    private sealed class TempPlatformInfo : PlatformInfoBase
    {
        public override PlatformKind Kind => PlatformKind.Windows;

        public override string ConfigDirectory { get; } = Path.Combine(
            Path.GetTempPath(), "claudestatus-platforminfo", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void Windows_uses_APPDATA()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        new WindowsPlatformInfo().ConfigDirectory.Should().Be(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClaudeStatus"));
    }

    [Fact]
    public void Windows_always_reports_a_tray()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        new WindowsPlatformInfo().TraySupport.Should().Be(TraySupport.Available);
    }

    [Fact]
    public void MacOS_uses_Library_Application_Support_not_dot_config()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        // .NET maps SpecialFolder.ApplicationData to ~/.config on macOS, which is
        // not where a mac user looks. The path is built explicitly for that reason.
        new MacOsPlatformInfo().ConfigDirectory
            .Should().EndWith(Path.Combine("Library", "Application Support", "ClaudeStatus"));
    }

    [Fact]
    public void Linux_honours_XDG_CONFIG_HOME()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only.");
        }

        string? original = Environment.GetEnvironmentVariable(LinuxPlatformInfo.XdgConfigHome);
        try
        {
            Environment.SetEnvironmentVariable(LinuxPlatformInfo.XdgConfigHome, "/tmp/xdg-probe");

            new LinuxPlatformInfo().ConfigDirectory.Should().Be("/tmp/xdg-probe/claudestatus");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LinuxPlatformInfo.XdgConfigHome, original);
        }
    }

    [Fact]
    public void Linux_falls_back_to_dot_config_when_XDG_is_unset()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only.");
        }

        string? original = Environment.GetEnvironmentVariable(LinuxPlatformInfo.XdgConfigHome);
        try
        {
            Environment.SetEnvironmentVariable(LinuxPlatformInfo.XdgConfigHome, null);

            new LinuxPlatformInfo().ConfigDirectory
                .Should().EndWith(Path.Combine(".config", "claudestatus"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(LinuxPlatformInfo.XdgConfigHome, original);
        }
    }

    [Fact]
    public void Linux_reports_no_tray_when_there_is_no_display_server()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only.");
        }

        string? display = Environment.GetEnvironmentVariable("DISPLAY");
        string? wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        try
        {
            Environment.SetEnvironmentVariable("DISPLAY", null);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);

            new LinuxPlatformInfo().TraySupport.Should().Be(TraySupport.Unavailable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", display);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", wayland);
        }
    }

    [Fact]
    public void Linux_flags_a_bare_GNOME_session_as_unlikely_to_have_a_tray()
    {
        // GNOME needs a third-party extension for StatusNotifierItem, and we
        // cannot detect it from here. Warn rather than refuse to start.
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only.");
        }

        string? display = Environment.GetEnvironmentVariable("DISPLAY");
        string? desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        try
        {
            Environment.SetEnvironmentVariable("DISPLAY", ":0");
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "GNOME");

            new LinuxPlatformInfo().TraySupport.Should().Be(TraySupport.Unlikely);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", display);
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", desktop);
        }
    }

    [Fact]
    public void Linux_assumes_a_tray_on_KDE()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only.");
        }

        string? display = Environment.GetEnvironmentVariable("DISPLAY");
        string? desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        try
        {
            Environment.SetEnvironmentVariable("DISPLAY", ":0");
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "KDE");

            new LinuxPlatformInfo().TraySupport.Should().Be(TraySupport.Available);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", display);
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", desktop);
        }
    }
}
