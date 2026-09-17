using System.Runtime.InteropServices;
using ClaudeStatus.Platform.MacOS;
using ClaudeStatus.Platform.MacOS.Interop;

namespace ClaudeStatus.Platform.Tests;

/// <summary>
/// The macOS notifier's parts that can be exercised without posting anything.
/// </summary>
/// <remarks>
/// Nothing here shows a notification, for the reason the Windows tests give: a
/// test that pops notifications at whoever runs it is a test people learn to skip.
/// The test host is not a bundle, which makes it exactly the <c>dotnet run</c> case.
/// </remarks>
public sealed class MacNotifierTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint BlockCopyFunction(nint block);

    /// <summary>The runtime's <c>_Block_copy</c>, reached without unsafe code.</summary>
    private static nint BlockCopy(nint block)
        => Marshal.GetDelegateForFunctionPointer<BlockCopyFunction>(
            NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_Block_copy"))(block);

    [Fact]
    public void Outside_a_bundle_it_is_unsupported_and_posts_nothing_rather_than_crashing()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        // Asking UNUserNotificationCenter for its center here would raise an
        // Objective-C exception and take the test host down with it.
        using var notifier = new MacNotifier();

        notifier.IsSupported.Should().BeFalse();
        notifier.Notify("title", "message", "tag").Should().BeFalse();
    }

    [Fact]
    public async Task A_completion_block_reports_success_when_the_framework_calls_it_with_no_error()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        (nint block, Task<string?> result) = MacNotifier.CreatePostCompletion();

        // The runtime's own copy must accept the hand-built layout: a global block
        // comes back as the same pointer.
        BlockCopy(block).Should().Be(block);

        ObjCBlock.Invoke(block, 0);

        (await result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            .Should().BeNull();

        // Not freed here. The callback queued the block for the notifier to free
        // later, as it must - the runtime reads a block again after invoking it -
        // and freeing it here as well is a double free in whichever test runs next.
    }

    [Fact]
    public void A_click_before_anyone_listens_reaches_the_first_subscriber_once()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        using var notifier = new MacNotifier();
        notifier.RaiseActivated("session-1");

        var first = new List<string?>();
        var second = new List<string?>();
        notifier.Activated += (_, e) => first.Add(e.Tag);
        notifier.Activated += (_, e) => second.Add(e.Tag);

        first.Should().Equal("session-1");
        second.Should().BeEmpty("the held click is delivered once, not to every later subscriber");

        notifier.RaiseActivated("session-2");
        first.Should().Equal("session-1", "session-2");
        second.Should().Equal("session-2");
    }
}
