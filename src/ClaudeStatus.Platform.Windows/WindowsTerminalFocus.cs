using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeStatus.Platform;
using ClaudeStatus.Sessions;

namespace ClaudeStatus.Platform.Windows;

/// <summary>
/// Finds the terminal or editor window a Claude Code session runs in, and focuses it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capture</b> runs in the hook process, whose ancestors are Claude Code, the
/// shell and - usually - the terminal. Three sources, best first:
/// </para>
/// <list type="number">
/// <item>
/// <b>The foreground window</b>, if it belongs to one of those processes. At
/// <c>UserPromptSubmit</c> the user has just pressed Enter in it, so it is the
/// exact window even when one process owns several (every Windows Terminal window
/// is one process; so is every VS Code window).
/// </item>
/// <item>
/// <b>The console.</b> Attaching to an ancestor's console and asking for its
/// window. Under classic conhost that is the visible window itself; under ConPTY
/// it is a hidden pseudo-window, which Windows Terminal gives an owner - its own
/// window. This is the case the ancestor walk cannot cover: a shell launched from
/// the Start menu and handed to Windows Terminal as the default terminal has
/// Explorer as its parent, not the terminal.
/// </item>
/// <item>
/// <b>The first visible window of the nearest ancestor that has one.</b> Right
/// process, possibly the wrong one of its windows.
/// </item>
/// </list>
/// <para>
/// The walk stops at <c>explorer.exe</c>, whose windows are the desktop and the
/// taskbar, and at a parent that started after its child, which means the real
/// parent is gone and its pid now belongs to something unrelated.
/// </para>
/// <para>
/// <b>Focus</b> re-checks the pid <i>and</i> its start time before touching the
/// window. Windows only lets a background process take the foreground when it has
/// just been given the right - a click on its own notification is such a moment -
/// so this must run synchronously inside that click. When Windows refuses anyway,
/// the taskbar button flashes instead.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsTerminalFocus : ITerminalFocus
{
    /// <summary>How far up the process tree to look. Hook, Claude Code, a shell or two, the terminal.</summary>
    private const int MaxDepth = 16;

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int SW_RESTORE = 9;
    private const uint FLASHW_ALL = 0x00000003;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;
    private static readonly nint InvalidHandle = -1;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public SessionOrigin? Capture()
    {
        try
        {
            List<Ancestor> chain = Ancestors();
            if (chain.Count == 0)
            {
                return null;
            }

            SessionOrigin? console = FromConsole(chain);

            nint foreground = GetForegroundWindow();
            if (foreground != 0 && IsTopLevelApplicationWindow(foreground))
            {
                int owner = ProcessOf(foreground);

                // FindIndex, not Find: Ancestor is a struct, and Find returns a
                // default one - pid 0 - when nothing matches.
                int match = chain.FindIndex(a => a.ProcessId == owner);
                if (match >= 0)
                {
                    Ancestor ancestor = chain[match];
                    return new SessionOrigin(
                        ancestor.ProcessId, ancestor.StartedAt, foreground, SessionOriginPrecision.Foreground);
                }

                if (console is not null && console.ProcessId == owner)
                {
                    return console with { Window = foreground, Precision = SessionOriginPrecision.Foreground };
                }
            }

            return console ?? FromProcessWindows(chain);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool TryFocus(SessionOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);

        try
        {
            // The pid must still be the process that was recorded. A pid is
            // recycled as soon as its process exits, and focusing whatever
            // inherited it would be focusing a stranger's window.
            if (StartTimeOf(origin.ProcessId) != origin.ProcessStartedAt)
            {
                return false;
            }

            nint window = (nint)origin.Window;

            // Window handles are recycled too. A closed terminal window whose
            // process lives on (another Windows Terminal window, another VS Code
            // window) falls back to that process's current front window.
            if (!IsWindow(window) || ProcessOf(window) != origin.ProcessId)
            {
                window = FirstWindowOf(origin.ProcessId);
                if (window == 0)
                {
                    return false;
                }
            }

            if (IsIconic(window))
            {
                ShowWindow(window, SW_RESTORE);
            }

            if (SetForegroundWindow(window))
            {
                return true;
            }

            // Refused: the click's permission to take the foreground did not
            // reach us. Flashing is the documented, always-permitted way to say
            // "over here".
            var flash = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = window,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = 3,
                dwTimeout = 0,
            };
            FlashWindowEx(ref flash);
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsForeground(SessionOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);

        try
        {
            nint foreground = GetForegroundWindow();
            if (foreground == 0 || ProcessOf(foreground) != origin.ProcessId)
            {
                return false;
            }

            // Same pid, but is it still the same process? See TryFocus.
            if (StartTimeOf(origin.ProcessId) != origin.ProcessStartedAt)
            {
                return false;
            }

            // One Windows Terminal or VS Code process owns every one of its
            // windows, so while the recorded window still exists it has to be
            // that window: another window of the same terminal is somewhere else.
            nint window = (nint)origin.Window;
            return !IsWindow(window) || ProcessOf(window) != origin.ProcessId || window == foreground;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>This process's ancestors, nearest first, stopping where the chain stops meaning anything.</summary>
    private static List<Ancestor> Ancestors()
    {
        Dictionary<int, (int Parent, string Image)> table = SnapshotProcesses();
        var chain = new List<Ancestor>();

        int child = Environment.ProcessId;
        DateTimeOffset? childStarted = StartTimeOf(child);

        for (int depth = 0; depth < MaxDepth && childStarted is not null; depth++)
        {
            if (!table.TryGetValue(child, out (int Parent, string Image) entry) || entry.Parent <= 0)
            {
                break;
            }

            int parent = entry.Parent;
            if (!table.TryGetValue(parent, out (int Parent, string Image) parentEntry)
                || string.Equals(parentEntry.Image, "explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            // Unreadable (elevated, or another user's) or younger than its child:
            // either way not the process that started us.
            DateTimeOffset? parentStarted = StartTimeOf(parent);
            if (parentStarted is null || parentStarted > childStarted)
            {
                break;
            }

            chain.Add(new Ancestor(parent, parentStarted.Value));
            child = parent;
            childStarted = parentStarted;
        }

        return chain;
    }

    /// <summary>The window of the console an ancestor is attached to, or the terminal that owns it.</summary>
    private static SessionOrigin? FromConsole(List<Ancestor> chain)
    {
        foreach (Ancestor ancestor in chain)
        {
            if (!AttachConsole((uint)ancestor.ProcessId))
            {
                continue;
            }

            nint console;
            try
            {
                console = GetConsoleWindow();
            }
            finally
            {
                FreeConsole();
            }

            // Every ancestor above this one is on the same console, or on none;
            // asking them again cannot give a different answer.
            if (console == 0)
            {
                return null;
            }

            // Classic conhost: the console window is the visible one. ConPTY: a
            // hidden pseudo-window, owned by the terminal's window where the
            // terminal bothers to say so.
            nint candidate = IsWindowVisible(console) ? console : GetWindow(console, GW_OWNER);
            if (candidate == 0 || !IsWindowVisible(candidate))
            {
                return null;
            }

            int owner = ProcessOf(candidate);
            return StartTimeOf(owner) is { } started
                ? new SessionOrigin(owner, started, candidate, SessionOriginPrecision.Console)
                : null;
        }

        return null;
    }

    /// <summary>The front window of the nearest ancestor that has one.</summary>
    private static SessionOrigin? FromProcessWindows(List<Ancestor> chain)
    {
        foreach (Ancestor ancestor in chain)
        {
            nint window = FirstWindowOf(ancestor.ProcessId);
            if (window != 0)
            {
                return new SessionOrigin(
                    ancestor.ProcessId, ancestor.StartedAt, window, SessionOriginPrecision.Process);
            }
        }

        return null;
    }

    /// <summary>
    /// The highest window in the z-order that belongs to <paramref name="processId"/> and a user would call a window.
    /// </summary>
    /// <remarks>
    /// <c>FindWindowEx</c> over the desktop walks top-level windows front to back,
    /// so the first match is the one the user used last. No <c>EnumWindows</c>
    /// callback to keep alive.
    /// </remarks>
    private static nint FirstWindowOf(int processId)
    {
        nint window = 0;
        while ((window = FindWindowExW(0, window, null, null)) != 0)
        {
            if (ProcessOf(window) == processId && IsTopLevelApplicationWindow(window))
            {
                return window;
            }
        }

        return 0;
    }

    /// <summary>Visible, unowned, not a tool window, and titled - what the taskbar would show.</summary>
    private static bool IsTopLevelApplicationWindow(nint window)
        => IsWindowVisible(window)
            && GetWindow(window, GW_OWNER) == 0
            && (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0
            && GetWindowTextLengthW(window) > 0;

    private static int ProcessOf(nint window)
    {
        _ = GetWindowThreadProcessId(window, out uint processId);
        return (int)processId;
    }

    /// <summary>When a process started, in UTC; null when it cannot be opened.</summary>
    private static DateTimeOffset? StartTimeOf(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            return GetProcessTimes(process, out long created, out _, out _, out _)
                ? DateTimeOffset.FromFileTime(created).ToUniversalTime()
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Every process's parent and image name, in one consistent snapshot.</summary>
    private static Dictionary<int, (int Parent, string Image)> SnapshotProcesses()
    {
        var table = new Dictionary<int, (int Parent, string Image)>();

        nint snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandle || snapshot == 0)
        {
            return table;
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                table[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile ?? string.Empty);
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return table;
    }

    private readonly record struct Ancestor(int ProcessId, DateTimeOffset StartedAt);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref PROCESSENTRY32W entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FLASHWINFO info);
}
