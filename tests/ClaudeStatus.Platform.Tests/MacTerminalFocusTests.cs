using System.Diagnostics;
using ClaudeStatus.Platform.MacOS;
using ClaudeStatus.Platform.MacOS.Interop;
using ClaudeStatus.Sessions;

namespace ClaudeStatus.Platform.Tests;

/// <summary>
/// Which ancestor stands for a session's terminal on macOS, against the process
/// trees real terminals produce. Pure, so it runs on every OS.
/// </summary>
public sealed class MacProcessTreeTests
{
    private const int Hook = 900;

    /// <summary>A process tree: pid → (parent, executable).</summary>
    private static int Find(params (int Pid, int Parent, string? Path)[] tree)
    {
        Dictionary<int, (int Parent, string? Path)> byPid = tree.ToDictionary(p => p.Pid, p => (p.Parent, p.Path));
        return MacProcessTree.FindTerminal(
            Hook,
            pid => byPid.TryGetValue(pid, out var p) ? p.Parent : null,
            pid => byPid.TryGetValue(pid, out var p) ? p.Path : null);
    }

    [Fact]
    public void Terminal_app_is_found_above_the_shell_and_login()
    {
        Find(
            (Hook, 800, "/Applications/ClaudeStatus.app/Contents/MacOS/ClaudeStatus"),
            (800, 700, "/Users/someone/.local/bin/claude"),
            (700, 600, "/bin/zsh"),
            (600, 500, "/usr/bin/login"),
            (500, 1, "/System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal"))
            .Should().Be(500);
    }

    [Fact]
    public void VS_Code_is_the_main_process_not_the_helper_in_its_nested_bundle()
    {
        Find(
            (Hook, 800, "/Applications/ClaudeStatus.app/Contents/MacOS/ClaudeStatus"),
            (800, 700, "/Users/someone/.local/bin/claude"),
            (700, 600, "/bin/zsh"),
            (600, 500, "/Applications/Visual Studio Code.app/Contents/Frameworks/Code Helper.app/Contents/MacOS/Code Helper"),
            (500, 1, "/Applications/Visual Studio Code.app/Contents/MacOS/Code"))
            .Should().Be(500);
    }

    [Fact]
    public void An_iTerm2_server_under_launchd_still_names_the_bundle()
    {
        Find(
            (Hook, 800, "/Applications/ClaudeStatus.app/Contents/MacOS/ClaudeStatus"),
            (800, 700, "/Users/someone/.local/bin/claude"),
            (700, 600, "/bin/zsh"),
            (600, 1, "/Applications/iTerm.app/Contents/MacOS/iTermServer-3.5.0"))
            .Should().Be(600);
    }

    [Fact]
    public void Climbing_stops_where_the_terminal_bundle_ends()
    {
        // A terminal started from another app's shell: the terminal is the answer,
        // not the app that happened to launch it.
        Find(
            (Hook, 800, "/Applications/ClaudeStatus.app/Contents/MacOS/ClaudeStatus"),
            (800, 700, "/Users/someone/.local/bin/claude"),
            (700, 600, "/Applications/Ghostty.app/Contents/MacOS/ghostty"),
            (600, 500, "/bin/zsh"),
            (500, 1, "/Applications/Visual Studio Code.app/Contents/MacOS/Code"))
            .Should().Be(700);
    }

    [Fact]
    public void No_bundle_among_the_ancestors_is_no_terminal()
    {
        // ssh, tmux under launchd, a cron job: nothing to bring forward.
        Find(
            (Hook, 800, "/Applications/ClaudeStatus.app/Contents/MacOS/ClaudeStatus"),
            (800, 700, "/Users/someone/.local/bin/claude"),
            (700, 600, "/bin/zsh"),
            (600, 1, "/usr/sbin/sshd"))
            .Should().Be(0);
    }

    [Fact]
    public void An_unreadable_parent_ends_the_walk_without_throwing()
    {
        Find((Hook, 800, null)).Should().Be(0);
    }

    [Theory]
    [InlineData("/Applications/Visual Studio Code.app/Contents/Frameworks/Code Helper.app/Contents/MacOS/Code Helper", "/Applications/Visual Studio Code.app")]
    [InlineData("/System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal", "/System/Applications/Utilities/Terminal.app")]
    [InlineData("/Applications/Foo.app/Contents/Resources/tool", null)]
    [InlineData("/usr/bin/login", null)]
    [InlineData(null, null)]
    public void The_outer_bundle_is_the_first_app_on_the_path(string? executable, string? expected)
        => MacProcessTree.OuterBundle(executable).Should().Be(expected);
}

/// <summary>
/// The macOS focus helper against real processes: the struct offsets it reads, and
/// its refusals. The success path - a click bringing a terminal forward - needs a
/// desktop and a person, so it lives in <c>docs/qa-checklist.md</c>.
/// </summary>
public sealed class MacTerminalFocusTests
{
    [Fact]
    public void The_process_info_offsets_describe_this_very_process()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        using Process self = Process.GetCurrentProcess();
        (int ParentId, DateTimeOffset StartedAt)? info = LibProc.Info(Environment.ProcessId);

        info.Should().NotBeNull();
        info!.Value.StartedAt.Should().BeCloseTo(self.StartTime.ToUniversalTime(), TimeSpan.FromSeconds(2));
        info.Value.ParentId.Should().BePositive();
        LibProc.Info(info.Value.ParentId).Should().NotBeNull("the parent read from the struct is a live process");
        Path.GetFileName(LibProc.ExecutablePath(Environment.ProcessId))
            .Should().Be(Path.GetFileName(Environment.ProcessPath));
    }

    [Fact]
    public void A_process_owned_by_another_user_is_still_described()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        // Every Terminal.app session has /usr/bin/login, running as root, between the
        // shell and Terminal. proc_pidinfo refused to describe it and the walk never
        // reached the terminal; launchd is the root-owned process every Mac has.
        (int ParentId, DateTimeOffset StartedAt)? launchd = LibProc.Info(1);

        launchd.Should().NotBeNull();
        launchd!.Value.ParentId.Should().Be(0, "launchd has no parent");
    }

    [Fact]
    public void Capture_never_throws_and_never_names_a_window()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        // Whatever runs the tests - a terminal locally, a service on CI - the
        // answer is a process-only origin or none.
        SessionOrigin? origin = new MacTerminalFocus().Capture();

        if (origin is not null)
        {
            origin.Window.Should().Be(0);
            origin.Precision.Should().Be(SessionOriginPrecision.Process);
            origin.ProcessId.Should().NotBe(Environment.ProcessId);
        }
    }

    [Fact]
    public void A_recycled_pid_is_neither_focused_nor_foreground()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        (int ParentId, DateTimeOffset StartedAt) info = LibProc.Info(Environment.ProcessId)!.Value;
        var origin = new SessionOrigin(
            Environment.ProcessId, info.StartedAt.AddMinutes(-1), 0, SessionOriginPrecision.Process);

        var focus = new MacTerminalFocus();
        focus.TryFocus(origin).Should().BeFalse("a pid whose start time differs belongs to some other process now");
        focus.IsForeground(origin).Should().BeFalse();
    }

    [Fact]
    public void A_process_that_does_not_exist_is_not_focused()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        // Above kern.maxproc, so never a live pid.
        var origin = new SessionOrigin(int.MaxValue - 2, DateTimeOffset.UtcNow, 0, SessionOriginPrecision.Process);

        new MacTerminalFocus().TryFocus(origin).Should().BeFalse();
    }
}
