using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeStatus.Platform;

namespace ClaudeStatus.Platform.Windows;

/// <summary>
/// Real Windows notifications, through the notification area.
/// </summary>
/// <remarks>
/// <para>
/// <c>Shell_NotifyIcon</c> with <c>NIF_INFO</c>. On Windows 10 and 11 the shell
/// renders that as a proper toast: it slides in over whatever the user is doing,
/// and it stays in the Action Centre afterwards, which is the whole difference
/// between this and the card the app draws for itself. Somebody who was in
/// another window when their session finished can still find out that it did.
/// </para>
/// <para>
/// <b>The fallback.</b> <see cref="WindowsToastNotifier"/> is preferred and needs
/// no icon, but it needs the app id a Velopack install registers, so it cannot work
/// on a machine where the app was unzipped rather than installed. This works for
/// any process that can own a window; <see cref="WindowsNotifier"/> chooses.
/// </para>
/// <para>
/// <b>The cost is an icon, for as long as a notification is up.</b> A balloon
/// belongs to a notification-area icon, and a hidden icon (<c>NIS_HIDDEN</c>) is
/// not allowed to show one. So the icon is added just before a balloon and deleted
/// when the shell reports that balloon hidden, timed out or clicked - the rest of
/// the time there is no second icon beside the indicator. A clicked entry left in
/// the notification centre after that may no longer reach us.
/// </para>
/// <para>
/// <b>Clicks.</b> The shell reports a click on the balloon as
/// <c>NIN_BALLOONUSERCLICK</c> through the icon's callback message, so the window
/// has a window procedure of ours. It must therefore be created on a thread that
/// pumps messages - the UI thread - or the click is never delivered. A legacy
/// balloon carries no identity of its own, so the notifier remembers the tag of
/// the last one it showed: clicking an older entry left in the Action Centre, if
/// the shell reports it at all, reports the newest tag.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class ShellNotifyIconNotifier : INotifier
{
    /// <summary>Our icon's id within this window. Any constant will do; it only has to be stable.</summary>
    private const uint IconId = 1;

    private const string ClassName = "ClaudeStatus.Notifier";

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;

    private const int IDI_APPLICATION = 32512;

    /// <summary>WM_APP + 1: the message the shell sends us about our icon.</summary>
    private const uint CallbackMessage = 0x8001;

    /// <summary>WM_USER + 3: the balloon was hidden for a reason other than a timeout or click.</summary>
    private const int NIN_BALLOONHIDE = 0x0403;

    /// <summary>WM_USER + 4: the balloon timed out or was dismissed.</summary>
    private const int NIN_BALLOONTIMEOUT = 0x0404;

    /// <summary>WM_USER + 5. Without NIM_SETVERSION it arrives as the callback's lParam.</summary>
    private const int NIN_BALLOONUSERCLICK = 0x0405;

    private const int GWLP_USERDATA = -21;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    private static readonly Lock ClassGate = new();
    private static bool _classRegistered;

    /// <summary>The window the icon belongs to. Message-only: never shown, never painted.</summary>
    private nint _window;
    private readonly nint _icon;
    private GCHandle _self;
    private string? _lastTag;
    private bool _added;
    private bool _disposed;

    /// <summary>Creates the hidden window. The icon waits for the first notification.</summary>
    /// <remarks>
    /// Failure is not thrown. A notifier that cannot be created reports
    /// <see cref="IsSupported"/> as false and the caller shows its card instead,
    /// which is a worse notification rather than no application.
    /// </remarks>
    public ShellNotifyIconNotifier()
    {
        try
        {
            if (!RegisterClass())
            {
                return;
            }

            // HWND_MESSAGE (-3) gives a window that exists only to receive
            // messages: no pixels, no taskbar button, no z-order. It is the
            // cheapest thing that can own a notification icon.
            _window = CreateWindowExW(
                0, ClassName, "ClaudeStatusNotifier", 0, 0, 0, 0, 0, -3, 0, GetModuleHandleW(null), 0);

            if (_window == 0)
            {
                return;
            }

            // The window procedure is static; this is how it finds its way back
            // to the instance that owns the window.
            _self = GCHandle.Alloc(this);
            SetWindowLongPtrW(_window, GWLP_USERDATA, GCHandle.ToIntPtr(_self));

            _icon = LoadIconW(0, IDI_APPLICATION);
        }
        catch (DllNotFoundException)
        {
            // Not a Windows worth supporting. IsSupported stays false.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <inheritdoc />
    public event EventHandler<NotificationActivatedEventArgs>? Activated;

    /// <inheritdoc />
    public bool IsSupported => _window != 0 && !_disposed;

    /// <inheritdoc />
    public bool Notify(string title, string message, string? tag = null)
    {
        if (!IsSupported)
        {
            return false;
        }

        try
        {
            if (!AddIcon())
            {
                return false;
            }

            NOTIFYICONDATAW data = Describe();
            data.uFlags = NIF_INFO;

            // The shell truncates both itself, but it truncates mid-character and
            // the struct is fixed width, so they are cut here where the cut can be
            // made somewhere sensible.
            data.szInfoTitle = Trim(title, 63);
            data.szInfo = Trim(message, 255);
            // NIIF_INFO alone. NIIF_USER asks the shell to draw hBalloonIcon, which
            // we do not set, and an unset one there is a documented way to get a
            // call that succeeds and shows nothing.
            data.dwInfoFlags = NIIF_INFO;

            bool shown = Shell_NotifyIconW(NIM_MODIFY, ref data);
            if (shown)
            {
                _lastTag = tag;
            }
            else
            {
                // Nothing is coming to take it away again.
                RemoveIcon();
            }

            return shown;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        RemoveIcon();

        if (_window != 0)
        {
            SetWindowLongPtrW(_window, GWLP_USERDATA, 0);
            DestroyWindow(_window);
            _window = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    /// <summary>Handles the shell's message about our icon.</summary>
    private void OnIconMessage(nint lParam)
    {
        int code = (int)(lParam & 0xFFFF);
        if (_disposed || code is not (NIN_BALLOONHIDE or NIN_BALLOONTIMEOUT or NIN_BALLOONUSERCLICK))
        {
            return;
        }

        try
        {
            if (code == NIN_BALLOONUSERCLICK)
            {
                // Synchronously, inside the shell's message: this is the moment
                // Windows lets the handler put another window in the foreground.
                Activated?.Invoke(this, new NotificationActivatedEventArgs(_lastTag));
            }
        }
        finally
        {
            // The balloon is gone either way, and the icon only existed for it.
            RemoveIcon();
        }
    }

    /// <summary>Adds the icon a balloon needs, unless one is already up.</summary>
    private bool AddIcon()
    {
        if (_added)
        {
            return true;
        }

        NOTIFYICONDATAW data = Describe();
        data.uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE;
        data.uCallbackMessage = CallbackMessage;

        // Deliberately no NIM_SETVERSION. Before this window had a procedure of its
        // own, asking for NOTIFYICON_VERSION_4 made every call succeed while no
        // toast was ever shown. Without it the balloon shows, and the callback's
        // lParam is the notification code itself, which is all OnIconMessage needs.
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        return _added;
    }

    /// <summary>Deletes the icon, if it is up.</summary>
    private void RemoveIcon()
    {
        if (!_added)
        {
            return;
        }

        NOTIFYICONDATAW data = Describe();
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>Registers the window class once per process.</summary>
    private static unsafe bool RegisterClass()
    {
        lock (ClassGate)
        {
            if (_classRegistered)
            {
                return true;
            }

            fixed (char* name = ClassName)
            {
                var info = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WindowProcedure,
                    hInstance = GetModuleHandleW(null),
                    lpszClassName = (nint)name,
                };

                // The class name is copied by the system, so the pinned string
                // does not have to outlive this call.
                _classRegistered = RegisterClassExW(ref info) != 0
                    || Marshal.GetLastPInvokeError() == ERROR_CLASS_ALREADY_EXISTS;
            }

            return _classRegistered;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            nint self = GetWindowLongPtrW(window, GWLP_USERDATA);
            if (self != 0 && GCHandle.FromIntPtr(self).Target is ShellNotifyIconNotifier notifier)
            {
                try
                {
                    notifier.OnIconMessage(lParam);
                }
                catch (Exception)
                {
                    // An exception must not unwind into the shell's message
                    // dispatch: under UnmanagedCallersOnly that is a process crash.
                }
            }

            return 0;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private NOTIFYICONDATAW Describe() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window,
        uID = IconId,
        hIcon = _icon,
        szTip = "ClaudeStatus",
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    /// <summary>Cuts to a length the fixed-width struct can hold, with an ellipsis when it bites.</summary>
    private static string Trim(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        /// <summary>A union with uTimeout, which is ignored from Vista on.</summary>
        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WNDCLASSEXW info);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint LoadIconW(nint instance, nint name);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
}
