using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeStatus.Platform.MacOS.Interop;

/// <summary>
/// What is needed to walk up a process tree: each process's parent and start time
/// (<c>sysctl</c>), and its executable (<c>libproc</c>).
/// </summary>
/// <remarks>
/// <para>
/// Plain syscalls rather than AppKit, because the caller is the hook process -
/// started by Claude Code at every turn, gone a moment later - and loading AppKit
/// there would cost more than everything else the hook does.
/// </para>
/// <para>
/// <b>Parent and start time come from <c>sysctl(KERN_PROC_PID)</c>, not
/// <c>proc_pidinfo</c>.</b> <c>proc_pidinfo</c> refuses to describe another user's
/// process, and every Terminal.app session has <c>/usr/bin/login</c>, running as
/// root, between the shell and Terminal: the walk stopped there and never reached
/// the terminal. <c>sysctl</c> answers for any process, which is how <c>ps</c> does
/// it. <c>proc_pidpath</c> has no such restriction.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe partial class LibProc
{
    private const string System = "/usr/lib/libSystem.B.dylib";

    // CTL_KERN, KERN_PROC, KERN_PROC_PID.
    private const int CtlKern = 1;
    private const int KernProc = 14;
    private const int KernProcPid = 1;

    /// <summary><c>sizeof(struct kinfo_proc)</c> on 64-bit macOS.</summary>
    private const int KinfoProcSize = 648;

    // Offsets into struct kinfo_proc (sys/sysctl.h), identical on arm64 and x86_64:
    // kp_proc.p_starttime (a timeval) opens the struct, kp_proc.p_pid is at 40, and
    // kp_eproc - which starts at 296 - holds e_ppid 264 bytes in.
    private const int StartSecondsOffset = 0;
    private const int StartMicrosecondsOffset = 8;
    private const int PidOffset = 40;
    private const int ParentOffset = 560;

    /// <summary><c>PROC_PIDPATHINFO_MAXSIZE</c>.</summary>
    private const int PathMax = 4096;

    [LibraryImport(System)]
    private static partial int sysctl(int* name, uint nameLength, byte* oldValue, nuint* oldLength, byte* newValue, nuint newLength);

    [LibraryImport(System)]
    private static partial int proc_pidpath(int pid, byte* buffer, uint size);

    /// <summary>A process's parent and start time, or null when there is no such process.</summary>
    internal static (int ParentId, DateTimeOffset StartedAt)? Info(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        int* name = stackalloc int[] { CtlKern, KernProc, KernProcPid, pid };
        byte* info = stackalloc byte[KinfoProcSize];
        nuint length = KinfoProcSize;

        // An exited pid is not an error to sysctl: it succeeds and writes nothing.
        if (sysctl(name, 4, info, &length, null, 0) != 0
            || length != KinfoProcSize
            || Unsafe.ReadUnaligned<int>(info + PidOffset) != pid)
        {
            return null;
        }

        long seconds = Unsafe.ReadUnaligned<long>(info + StartSecondsOffset);
        int microseconds = Unsafe.ReadUnaligned<int>(info + StartMicrosecondsOffset);

        return (
            Unsafe.ReadUnaligned<int>(info + ParentOffset),
            DateTimeOffset.UnixEpoch.AddTicks(
                (seconds * TimeSpan.TicksPerSecond) + (microseconds * TimeSpan.TicksPerMicrosecond)));
    }

    /// <summary>A process's executable, or null when it cannot be read.</summary>
    internal static string? ExecutablePath(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        byte* buffer = stackalloc byte[PathMax];
        int length = proc_pidpath(pid, buffer, PathMax);
        return length > 0 ? Marshal.PtrToStringUTF8((nint)buffer, length) : null;
    }
}
