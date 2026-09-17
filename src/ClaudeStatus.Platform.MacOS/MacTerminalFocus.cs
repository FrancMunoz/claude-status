using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeStatus.Platform.MacOS.Interop;
using ClaudeStatus.Sessions;

namespace ClaudeStatus.Platform.MacOS;

/// <summary>
/// Finds the application a Claude Code session runs in, and brings it forward.
/// </summary>
/// <remarks>
/// <para>
/// <b>An application, not a window.</b> Choosing one window of another application
/// needs the Accessibility permission, which a usage meter has no business asking
/// for. So the origin is a process with <see cref="SessionOriginPrecision.Process"/>
/// and no window, and a click activates that application: with two Terminal windows
/// open, the one macOS last had in front comes forward.
/// </para>
/// <para>
/// <see cref="Capture"/> runs in the hook process and uses <c>libproc</c> only.
/// <see cref="TryFocus"/> and <see cref="IsForeground"/> run in the app, where AppKit
/// is already loaded, and check the recorded pid's start time before trusting it: a
/// pid is reused once its process exits.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacTerminalFocus : ITerminalFocus
{
    /// <summary><c>NSApplicationActivateAllWindows</c>.</summary>
    private const long ActivateAllWindows = 1 << 0;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public SessionOrigin? Capture()
    {
        try
        {
            int terminal = MacProcessTree.FindTerminal(
                Environment.ProcessId,
                pid => LibProc.Info(pid)?.ParentId,
                LibProc.ExecutablePath);

            return terminal != 0 && LibProc.Info(terminal) is { } info
                ? new SessionOrigin(terminal, info.StartedAt, Window: 0, SessionOriginPrecision.Process)
                : null;
        }
        catch (Exception)
        {
            // Never throws, per the interface: no origin just means no focusing.
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tried first as <c>activateWithOptions:</c>. Should that be refused - macOS
    /// 14 only lets an app hand activation on while it is active itself - the
    /// application is opened again through <c>NSWorkspace</c>, which brings an
    /// already running one to the front just as clicking it in the Dock does.
    /// </remarks>
    public bool TryFocus(SessionOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);

        try
        {
            nint app = RunningApplication(origin);
            if (app == 0)
            {
                return false;
            }

            if ((ObjC.SendLong(app, ObjC.sel_registerName("activateWithOptions:"), ActivateAllWindows) & 0xFF) != 0)
            {
                return true;
            }

            nint url = ObjC.Send(app, "bundleURL");
            nint configuration = ObjC.Class("NSWorkspaceOpenConfiguration") is var cls and not 0
                ? ObjC.Send(cls, "configuration")
                : 0;

            if (url == 0 || configuration == 0)
            {
                return false;
            }

            ObjC.Send(
                ObjC.Send(ObjC.Class("NSWorkspace"), "sharedWorkspace"),
                ObjC.sel_registerName("openApplicationAtURL:configuration:completionHandler:"),
                url,
                configuration,
                0);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Whether the terminal application is the frontmost one - any of its windows.
    /// Coarser than on Windows, for the reason <see cref="MacTerminalFocus"/> gives.
    /// </remarks>
    public bool IsForeground(SessionOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);

        try
        {
            nint app = RunningApplication(origin);
            if (app == 0)
            {
                return false;
            }

            nint frontmost = ObjC.Send(ObjC.Send(ObjC.Class("NSWorkspace"), "sharedWorkspace"), "frontmostApplication");
            return frontmost != 0 && ProcessIdOf(frontmost) == ProcessIdOf(app);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The running application the origin stands for, or 0 when the recorded process
    /// is gone, is a different process now, or belongs to no running application.
    /// </summary>
    private static nint RunningApplication(SessionOrigin origin)
    {
        if (LibProc.Info(origin.ProcessId) is not { } info || info.StartedAt != origin.ProcessStartedAt)
        {
            return 0;
        }

        string? bundle = MacProcessTree.OuterBundle(LibProc.ExecutablePath(origin.ProcessId));
        if (bundle is null)
        {
            return 0;
        }

        NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out _);
        nint runningClass = ObjC.Class("NSRunningApplication");
        if (runningClass == 0)
        {
            return 0;
        }

        // The recorded process is usually the application itself (Terminal, VS Code).
        nint byPid = ObjC.SendLong(
            runningClass, ObjC.sel_registerName("runningApplicationWithProcessIdentifier:"), origin.ProcessId);
        if (byPid != 0 && string.Equals(BundlePath(byPid), bundle, StringComparison.Ordinal))
        {
            return byPid;
        }

        // Otherwise a helper that shares its bundle (iTerm2's server): find the
        // application by the bundle's identifier.
        nint nsBundle = ObjC.Send(ObjC.Class("NSBundle"), "bundleWithPath:", ObjC.NSString(bundle));
        nint identifier = nsBundle == 0 ? 0 : ObjC.Send(nsBundle, "bundleIdentifier");
        if (identifier == 0)
        {
            return 0;
        }

        nint apps = ObjC.Send(runningClass, "runningApplicationsWithBundleIdentifier:", identifier);
        long count = apps == 0 ? 0 : ObjC.SendReturningLong(apps, ObjC.sel_registerName("count"));
        for (long i = 0; i < count; i++)
        {
            nint candidate = ObjC.SendLong(apps, ObjC.sel_registerName("objectAtIndex:"), i);
            if (string.Equals(BundlePath(candidate), bundle, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return 0;
    }

    private static string? BundlePath(nint runningApplication)
    {
        nint url = ObjC.Send(runningApplication, "bundleURL");
        return url == 0 ? null : ObjC.ManagedString(ObjC.Send(url, "path"));
    }

    private static int ProcessIdOf(nint runningApplication)
        => (int)ObjC.SendReturningLong(runningApplication, ObjC.sel_registerName("processIdentifier"));
}
