using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeStatus.Platform;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ClaudeStatus.Platform.Windows;

/// <summary>
/// Reads whether the Windows taskbar is light or dark, and says when that changes.
/// </summary>
/// <remarks>
/// <para>
/// Windows keeps two separate theme settings. <c>SystemUsesLightTheme</c> is the
/// one that governs the taskbar and notification area; <c>AppsUseLightTheme</c>
/// governs application windows and is <b>not</b> what we want — a user can very
/// reasonably run light apps on a dark taskbar, and reading the wrong key would
/// invert the icon for them.
/// </para>
/// <para>
/// <see cref="Current"/> is read fresh on every call. The value sits in the
/// registry's memory-backed user hive, so this is far cheaper than the
/// once-a-minute call rate makes it look.
/// </para>
/// <para>
/// <see cref="Changed"/> comes from <c>RegNotifyChangeKeyValue</c> on the same
/// key, not from <c>WM_SETTINGCHANGE</c>. That message is broadcast to top-level
/// windows only, and the taskbar widget is a child of the taskbar; Avalonia's own
/// colour-change event follows <c>AppsUseLightTheme</c> and so misses a switch of
/// the taskbar alone. The registry watch needs neither a window nor a thread of
/// its own: the notification signals an event, and a thread-pool wait picks it up.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsTrayThemeProvider : ITrayThemeProvider, IDisposable
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    internal const string SystemThemeValue = "SystemUsesLightTheme";

    /// <summary>A value in the key was set, added or deleted.</summary>
    private const int RegNotifyChangeLastSet = 0x00000004;

    /// <summary>
    /// The watch outlives the thread that armed it.
    /// </summary>
    /// <remarks>
    /// Without this the watch dies with the arming thread, and ours is re-armed from
    /// thread-pool callbacks whose threads come and go. Windows 8 and later.
    /// </remarks>
    private const int RegNotifyThreadAgnostic = 0x10000000;

    /// <summary>The key read and watched, under <c>HKCU</c>.</summary>
    private readonly string _keyPath;

    private readonly Lock _gate = new();
    private EventHandler? _changed;
    private RegistryKey? _key;
    private AutoResetEvent? _signal;
    private RegisteredWaitHandle? _wait;
    private TrayBackground _last;
    private bool _disposed;

    /// <summary>Reads and watches the real taskbar setting.</summary>
    public WindowsTrayThemeProvider()
        : this(PersonalizeKey)
    {
    }

    /// <summary>Reads and watches another key, so tests never flip the user's theme.</summary>
    internal WindowsTrayThemeProvider(string keyPath) => _keyPath = keyPath;

    /// <inheritdoc />
    public TrayBackground Current
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath);
                return Interpret(key?.GetValue(SystemThemeValue));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException
                or UnauthorizedAccessException or IOException)
            {
                return TrayBackground.Unknown;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The watch starts with the first subscriber and stops with the last, so a
    /// provider nobody listens to holds no handles. Raised on a thread-pool thread,
    /// and only when the taskbar actually changed - the key holds other values too,
    /// accent colour and transparency among them.
    /// </remarks>
    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _changed += value;
                if (_changed is not null && _wait is null)
                {
                    StartWatching();
                }
            }
        }

        remove
        {
            lock (_gate)
            {
                _changed -= value;
                if (_changed is null)
                {
                    StopWatching();
                }
            }
        }
    }

    private static TrayBackground Interpret(object? value) => value switch
    {
        int usesLight => usesLight != 0 ? TrayBackground.Light : TrayBackground.Dark,

        // The key is absent on some older or policy-managed installs.
        // Dark has been the taskbar default since Windows 10, but
        // "Unknown" makes the renderer outline the glyph, which is
        // correct either way. Guessing would be worse.
        _ => TrayBackground.Unknown,
    };

    /// <summary>Opens the key and arms the first notification. Caller holds the gate.</summary>
    private void StartWatching()
    {
        try
        {
            // Read access includes KEY_NOTIFY.
            _key = Registry.CurrentUser.OpenSubKey(_keyPath);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
            or UnauthorizedAccessException or IOException)
        {
            _key = null;
        }

        if (_key is null)
        {
            // Nothing to watch, and Current reports Unknown for as long as that
            // lasts. The next poll still re-reads it, as before this existed.
            return;
        }

        _signal = new AutoResetEvent(false);
        _last = Current;
        if (!Arm())
        {
            StopWatching();
            return;
        }

        _wait = ThreadPool.RegisterWaitForSingleObject(
            _signal, OnSignalled, state: null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Asks for one notification. Each one is spent when it fires.</summary>
    private bool Arm()
        => _key is not null && _signal is not null
            && RegNotifyChangeKeyValue(
                _key.Handle,
                watchSubtree: false,
                RegNotifyChangeLastSet | RegNotifyThreadAgnostic,
                _signal.SafeWaitHandle,
                asynchronous: true) == 0;

    private void OnSignalled(object? state, bool timedOut)
    {
        EventHandler? handlers;
        lock (_gate)
        {
            if (_wait is null)
            {
                // Stopped while this callback was already queued.
                return;
            }

            // Re-arm before reading, so a change landing between the two is caught
            // by the next notification rather than lost.
            if (!Arm())
            {
                StopWatching();
                return;
            }

            TrayBackground now = Current;
            if (now == _last)
            {
                return;
            }

            _last = now;
            handlers = _changed;
        }

        // Outside the gate: a handler that unsubscribes must not deadlock.
        handlers?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tears the watch down. Caller holds the gate.</summary>
    private void StopWatching()
    {
        _wait?.Unregister(null);
        _wait = null;

        // Closing the key cancels the pending notification.
        _key?.Dispose();
        _key = null;
        _signal?.Dispose();
        _signal = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _changed = null;
            StopWatching();
        }
    }

    [LibraryImport("advapi32.dll")]
    private static partial int RegNotifyChangeKeyValue(
        SafeRegistryHandle key,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        int notifyFilter,
        SafeWaitHandle eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
