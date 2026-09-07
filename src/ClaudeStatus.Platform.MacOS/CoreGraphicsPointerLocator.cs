using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeStatus.Platform;

namespace ClaudeStatus.Platform.MacOS;

/// <summary>
/// Reads the pointer position from CoreGraphics.
/// </summary>
/// <remarks>
/// <para>
/// CoreGraphics rather than AppKit on purpose. <c>NSEvent.mouseLocation</c> is the
/// obvious call and reaching it from .NET means hand-rolled Objective-C runtime
/// interop - <c>objc_getClass</c>, selector lookups, and a struct return whose ABI
/// differs between arm64 and x64. <c>CGEventGetLocation</c> answers the same
/// question through two plain C functions.
/// </para>
/// <para>
/// The other reason is the coordinate system. AppKit measures from the bottom-left
/// of the main screen with y going up; CoreGraphics measures from the top-left with
/// y going down, which is the convention Avalonia's <c>Screen</c> and
/// <c>Window.Position</c> already use. Only x is wanted here, so the difference
/// would not have bitten - but it would have been waiting for whoever wanted y.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed partial class CoreGraphicsPointerLocator : ITrayPointerLocator
{
    private const string CoreGraphics =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <inheritdoc />
    /// <remarks>
    /// Returns null rather than throwing if the event cannot be created. That
    /// happens when the process has no window server connection - a headless or
    /// daemon context - and there the honest answer is "no pointer", not a crash
    /// on the way to opening a window.
    /// </remarks>
    public double? PointerX
    {
        get
        {
            // A null source is documented as "the current state of the system",
            // which is exactly the question being asked.
            nint handle = CGEventCreate(0);
            if (handle == 0)
            {
                return null;
            }

            try
            {
                return CGEventGetLocation(handle).X;
            }
            finally
            {
                // Create-rule: this event is owned here and leaks without it. The
                // popup opens on every tray click, so a leak per click would be a
                // slow one but a real one.
                CFRelease(handle);
            }
        }
    }

    [LibraryImport(CoreGraphics)]
    private static partial nint CGEventCreate(nint source);

    [LibraryImport(CoreGraphics)]
    private static partial CGPoint CGEventGetLocation(nint eventReference);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint reference);

    /// <summary>The CoreGraphics <c>CGPoint</c>: two native-width floats.</summary>
    /// <remarks>
    /// <c>CGFloat</c> is 64-bit on every platform .NET runs macOS on, both arm64
    /// and x64, so <see cref="double"/> is the correct width rather than a
    /// convenient guess.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGPoint(double X, double Y);
}
