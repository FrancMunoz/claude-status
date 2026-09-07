namespace ClaudeStatus.Platform;

/// <summary>
/// An axis-aligned box in physical screen pixels, right and bottom exclusive.
/// </summary>
/// <remarks>
/// Its own type rather than a platform rectangle because Core has no platform
/// (<c>docs/manual.md</c> §6), and because the taskbar arithmetic in
/// <see cref="TaskbarLayout"/> is worth testing without a taskbar.
/// </remarks>
public readonly record struct PixelBox(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Width in pixels; zero or negative when the box is degenerate.</summary>
    public int Width => Right - Left;

    /// <summary>Height in pixels; zero or negative when the box is degenerate.</summary>
    public int Height => Bottom - Top;

    /// <summary>Whether the box encloses no pixels at all.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// What the taskbar looks like right now, as measured by the platform.
/// </summary>
/// <param name="Taskbar">The whole taskbar, in screen pixels.</param>
/// <param name="NotifyArea">
/// The system tray - clock, icons - in screen pixels, or null when the platform
/// could not find it. The widget sits immediately to its left.
/// </param>
/// <param name="Dpi">The taskbar's DPI, 96 meaning no scaling.</param>
public sealed record TaskbarMetrics(PixelBox Taskbar, PixelBox? NotifyArea, int Dpi)
{
    /// <summary>
    /// Whether the taskbar runs along the top or bottom edge of the screen.
    /// </summary>
    /// <remarks>
    /// Judged from its shape, the way TrafficMonitor does: wider than tall means
    /// horizontal. A vertical taskbar is not supported by the widget yet.
    /// </remarks>
    public bool IsHorizontal => Taskbar.Width >= Taskbar.Height;
}

/// <summary>
/// Where the widget goes, in pixels relative to the taskbar's top-left corner.
/// </summary>
public sealed record TaskbarSlot(int X, int Y, int Width, int Height);

/// <summary>
/// Puts a window of ours inside the operating system's taskbar.
/// </summary>
/// <remarks>
/// <para>
/// This is the TrafficMonitor technique: the taskbar is an ordinary window, so a
/// window of ours can be re-parented into it and positioned like any other child.
/// Nothing about it is a public API, which is why every method here can fail and
/// why the caller must keep re-measuring - Explorer relays out its taskbar
/// constantly and restarts it occasionally.
/// </para>
/// <para>
/// Handles are <see cref="nint"/> rather than a platform type so Core stays free
/// of platform references. Only the Windows implementation does anything; every
/// other platform gets <see cref="UnsupportedTaskbarHost"/>, and the app falls
/// back to the tray icon.
/// </para>
/// </remarks>
public interface ITaskbarHost
{
    /// <summary>
    /// Whether there is a taskbar here that the widget could live in.
    /// </summary>
    /// <remarks>
    /// Cheap and side-effect free: it is asked before any window is created so
    /// the app can pick the tray icon instead without ever having shown a window.
    /// </remarks>
    bool IsSupported { get; }

    /// <summary>Measures the taskbar. Null when it cannot be found right now.</summary>
    TaskbarMetrics? Measure();

    /// <summary>Makes the window a child of the taskbar.</summary>
    /// <returns>False when there is no taskbar to join or the platform refused.</returns>
    bool Attach(nint windowHandle);

    /// <summary>
    /// Whether the window is still inside a live taskbar.
    /// </summary>
    /// <remarks>
    /// Goes false when Explorer restarts: the old taskbar window is destroyed and
    /// ours is orphaned along with it, so the caller has to <see cref="Attach"/>
    /// again to the new one.
    /// </remarks>
    bool IsAttached(nint windowHandle);

    /// <summary>Moves and sizes the attached window within the taskbar.</summary>
    void Move(nint windowHandle, TaskbarSlot slot);

    /// <summary>Takes the window back out of the taskbar, if it is in one.</summary>
    void Detach(nint windowHandle);
}

/// <summary>The host for platforms without a taskbar we can join.</summary>
public sealed class UnsupportedTaskbarHost : ITaskbarHost
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public TaskbarMetrics? Measure() => null;

    /// <inheritdoc />
    public bool Attach(nint windowHandle) => false;

    /// <inheritdoc />
    public bool IsAttached(nint windowHandle) => false;

    /// <inheritdoc />
    public void Move(nint windowHandle, TaskbarSlot slot)
    {
    }

    /// <inheritdoc />
    public void Detach(nint windowHandle)
    {
    }
}
