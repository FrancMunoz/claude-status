using System.Diagnostics;
using ClaudeStatus.Platform.Windows;
using ClaudeStatus.Sessions;

namespace ClaudeStatus.Platform.Tests;

/// <summary>
/// The Windows terminal focus helper's refusals.
/// </summary>
/// <remarks>
/// The success path - a click bringing a real terminal forward - needs a desktop,
/// a terminal and a person, so it lives in <c>docs/qa-checklist.md</c>. What can
/// be tested here is everything that must make it do nothing.
/// </remarks>
public sealed class TerminalFocusTests
{
    [Fact]
    public void Capture_never_throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        // Whatever the test runner's ancestry is - a terminal locally, a service
        // on CI - the answer is an origin or null, never an exception.
        SessionOrigin? origin = new WindowsTerminalFocus().Capture();

        if (origin is not null)
        {
            origin.ProcessId.Should().NotBe(Environment.ProcessId, "the hook itself has no window worth focusing");
            origin.Window.Should().NotBe(0);
        }
    }

    [Fact]
    public void A_recycled_pid_is_not_focused()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        using Process self = Process.GetCurrentProcess();
        var origin = new SessionOrigin(
            self.Id,
            self.StartTime.ToUniversalTime().AddMinutes(-1),
            0x1234,
            SessionOriginPrecision.Foreground);

        new WindowsTerminalFocus().TryFocus(origin)
            .Should().BeFalse("a pid whose start time differs belongs to some other process now");
    }

    [Fact]
    public void A_process_that_does_not_exist_is_not_focused()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        // Pids are multiples of four on Windows; this one is not, so it is never live.
        var origin = new SessionOrigin(int.MaxValue - 2, DateTimeOffset.UtcNow, 0x1234, SessionOriginPrecision.Process);

        new WindowsTerminalFocus().TryFocus(origin).Should().BeFalse();
    }

    [Fact]
    public void The_null_helper_does_nothing()
    {
        var focus = new NullTerminalFocus();

        focus.IsSupported.Should().BeFalse();
        focus.Capture().Should().BeNull();
        focus.TryFocus(new SessionOrigin(1, DateTimeOffset.UtcNow, 1, SessionOriginPrecision.Process)).Should().BeFalse();
    }
}
