using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeStatus.Platform;

namespace ClaudeStatus.Platform.Windows;

/// <summary>
/// Puts a window inside Explorer's taskbar, the way TrafficMonitor does.
/// </summary>
/// <remarks>
/// <para>
/// The taskbar is a top-level window of class <c>Shell_TrayWnd</c>. Its system
/// tray - clock and icons - is a child of class <c>TrayNotifyWnd</c> on both
/// Windows 10 and 11, which is why the widget anchors to it: the running-apps
/// strip is a Win32 child (<c>MSTaskSwWClass</c>) on 10 and a XAML island on 11,
/// but the tray has kept its window class through both.
/// </para>
/// <para>
/// <b>None of this is a public API.</b> The class names are undocumented and
/// have survived since Windows XP, which is the only guarantee there is. Every
/// call here can fail, and the caller treats a failure as "fall back to the tray
/// icon", never as an error.
/// </para>
/// <para>
/// Windows 10 is not given the TrafficMonitor treatment of shrinking the task
/// list to make room. The widget overlays the right end of the task strip
/// instead, exactly as it must on Windows 11, where there is nothing to shrink.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsTaskbarHost : ITaskbarHost
{
    private const string TaskbarClass = "Shell_TrayWnd";
    private const string TrayClass = "TrayNotifyWnd";

    /// <inheritdoc />
    public bool IsSupported
    {
        get
        {
            nint taskbar = FindWindowW(TaskbarClass, null);
            if (taskbar == 0 || !GetWindowRect(taskbar, out Rect rect))
            {
                return false;
            }

            var box = ToBox(rect);
            return !box.IsEmpty && box.Width >= box.Height;
        }
    }

    /// <inheritdoc />
    public TaskbarMetrics? Measure()
    {
        nint taskbar = FindWindowW(TaskbarClass, null);
        if (taskbar == 0 || !GetWindowRect(taskbar, out Rect barRect))
        {
            return null;
        }

        PixelBox? tray = null;
        nint notify = FindWindowExW(taskbar, 0, TrayClass, null);
        if (notify != 0 && GetWindowRect(notify, out Rect trayRect))
        {
            tray = ToBox(trayRect);
        }

        uint dpi = GetDpiForWindow(taskbar);
        return new TaskbarMetrics(ToBox(barRect), tray, dpi == 0 ? 96 : (int)dpi);
    }

    /// <inheritdoc />
    public bool Attach(nint windowHandle)
    {
        nint taskbar = FindWindowW(TaskbarClass, null);
        if (taskbar == 0 || windowHandle == 0)
        {
            return false;
        }

        // Alt+Tab lists the widget otherwise. Re-parenting into the taskbar does
        // not set WS_CHILD, so as far as the shell is concerned this is still a
        // top-level window with a title, and ShowInTaskbar="False" only removes
        // the taskbar button - the switcher reads WS_EX_TOOLWINDOW instead. The
        // flag has to go on while the window is hidden or the entry already in
        // the switcher stays there; Move() shows it again straight after.
        _ = ShowWindow(windowHandle, SwHide);
        int style = GetWindowLongW(windowHandle, GwlExStyle);
        _ = SetWindowLongW(windowHandle, GwlExStyle, style | WsExToolWindow);

        // SetParent returns the previous parent, or 0 on failure. A window that
        // had no parent also yields 0 on success, so check the result instead -
        // and with GetAncestor, because GetParent answers "owner" for a popup
        // window and reported this very re-parenting as a failure.
        _ = SetParent(windowHandle, taskbar);
        return GetAncestor(windowHandle, GaParent) == taskbar;
    }

    /// <inheritdoc />
    public bool IsAttached(nint windowHandle)
    {
        if (windowHandle == 0 || !IsWindow(windowHandle))
        {
            return false;
        }

        nint parent = GetAncestor(windowHandle, GaParent);
        return parent != 0 && IsWindow(parent) && parent == FindWindowW(TaskbarClass, null);
    }

    /// <inheritdoc />
    public void Move(nint windowHandle, TaskbarSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        if (windowHandle == 0)
        {
            return;
        }

        // Child coordinates are relative to the parent's client area, which for
        // the taskbar is the taskbar itself. No activation: this must never
        // steal focus from whatever the user is typing into.
        _ = SetWindowPos(
            windowHandle,
            HwndTop,
            slot.X,
            slot.Y,
            slot.Width,
            slot.Height,
            SwpNoActivate | SwpShowWindow);
    }

    /// <inheritdoc />
    public void Detach(nint windowHandle)
    {
        if (windowHandle != 0 && IsWindow(windowHandle))
        {
            _ = SetParent(windowHandle, 0);
        }
    }

    private static PixelBox ToBox(Rect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private const nint HwndTop = 0;
    private const uint GaParent = 1;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x0000_0080;
    private const int SwHide = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string? className, string? windowName);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint SetParent(nint child, nint newParent);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    // The 32-bit form of the window-long pair, not the Ptr one: an extended
    // style is an int on every architecture, and these two are exported by
    // 32-bit Windows as well.
    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowLongW(nint hwnd, int index, int value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);
}
