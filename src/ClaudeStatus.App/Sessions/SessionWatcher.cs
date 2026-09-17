using ClaudeStatus.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeStatus.App.Sessions;

/// <summary>Something happened to a session that the app may want to say out loud.</summary>
/// <param name="change">What happened.</param>
/// <param name="session">The session it happened to.</param>
public sealed class SessionChangedEventArgs(SessionChange change, ClaudeSession session) : EventArgs
{
    /// <summary>What happened.</summary>
    public SessionChange Change { get; } = change;

    /// <summary>The session it happened to.</summary>
    public ClaudeSession Session { get; } = session;
}

/// <summary>
/// Turns spooled hook events into a live picture of what is running.
/// </summary>
/// <remarks>
/// <para>
/// Owns the three moving parts and nothing else: the hooks in Claude Code's
/// settings, the spool directory they write to, and the registry that remembers
/// what they said. <see cref="SessionRegistry"/> does the thinking; this does the
/// plumbing.
/// </para>
/// <para>
/// <b>The hooks live exactly as long as this object does.</b> They go in when it
/// starts and come out when it stops, so a machine where ClaudeStatus is not
/// running is a machine where Claude Code has no hooks of ours - nothing spawns,
/// nothing is written, and there is no notification from an app that is not
/// there. That is also what keeps an uninstall clean.
/// </para>
/// <para>
/// A <see cref="FileSystemWatcher"/> is the fast path, not the only one. They are
/// known to miss events under load and to die quietly when a directory is
/// replaced, and a missed turn here is a notification that never arrives, so a
/// slow timer drains the spool regardless.
/// </para>
/// </remarks>
public sealed class SessionWatcher : IDisposable
{
    /// <summary>How often the spool is drained even if the watcher says nothing.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(20);

    /// <summary>How long to wait after a file appears, so a burst is read in one go.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    private readonly IHookManager _hooks;
    private readonly SessionSpool _spool;
    private readonly SessionRegistry _registry;
    private readonly SessionStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly Lock _gate = new();

    /// <summary>
    /// Serialises <see cref="Drain"/>.
    /// </summary>
    /// <remarks>
    /// A file appearing raises both <c>Created</c> and <c>Changed</c>, and the
    /// sweep can land on top of either, so drains genuinely overlap. Two of them
    /// on the same directory is not merely wasteful: one reads a file the other
    /// has already deleted, the read throws, and <b>the event is lost</b>. That is
    /// what made sessions stay "running" for ever - the <c>SessionEnd</c> that
    /// would have finished them was the one being eaten.
    /// </remarks>
    private readonly Lock _drainGate = new();

    private FileSystemWatcher? _watcher;
    private ITimer? _sweep;
    private bool _started;
    private bool _disposed;

    public SessionWatcher(
        IHookManager hooks,
        SessionSpool spool,
        SessionRegistry registry,
        SessionStore store,
        TimeProvider clock,
        ILogger<SessionWatcher>? log = null)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _spool = spool ?? throw new ArgumentNullException(nameof(spool));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? NullLogger<SessionWatcher>.Instance;
    }

    /// <summary>Raised for every session event worth reacting to.</summary>
    public event EventHandler<SessionChangedEventArgs>? Changed;

    /// <summary>Whether the watcher is running.</summary>
    public bool IsRunning => _started;

    /// <summary>The sessions to show, running first.</summary>
    public IReadOnlyList<ClaudeSession> Sessions => _registry.Snapshot(_clock.GetUtcNow());

    /// <summary>Installs the hooks and starts listening.</summary>
    public async Task<HookSyncResult> StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        HookSyncResult result = await _hooks.SyncAsync(enabled: true, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _log.LogWarning(
                "Session hooks were not installed ({Reason}); no sessions will be listed.",
                result.MessageKey);
            return result;
        }

        _spool.Ensure();

        // The list as it was when the app last stopped. Sessions outlive us, so
        // starting from nothing would report an empty machine rather than an
        // unobserved one - and a session busy on a long task would stay invisible
        // for exactly as long as it was busy, since nothing returns until its
        // next turn.
        _registry.Restore(_store.Load(), _clock.GetUtcNow());

        // Anything left from a previous run, drained before the watcher starts so
        // the two cannot both deliver the same file.
        Drain();

        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(_spool.Directory, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };

            _watcher.Created += OnSpoolChanged;
            _watcher.Changed += OnSpoolChanged;

            // A watcher that dies takes the feature with it silently. The sweep
            // below is what makes that survivable, so the error is logged and
            // then deliberately not treated as fatal.
            _watcher.Error += (_, e) => _log.LogWarning(
                e.GetException(), "The session spool watcher failed. Falling back to the sweep.");

            _sweep = _clock.CreateTimer(_ => Drain(), null, SweepInterval, SweepInterval);
            _started = true;
        }

        _log.LogInformation("Session watch started. Spool: {Directory}.", _spool.Directory);
        return result;
    }

    /// <summary>Stops listening and takes the hooks back out.</summary>
    /// <remarks>
    /// Removal is the whole point of stopping: leave the hooks in and Claude Code
    /// keeps launching this executable for an app that is not running.
    /// </remarks>
    public async Task StopAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _started = false;

            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }

            _sweep?.Dispose();
            _sweep = null;
        }

        try
        {
            await _hooks.SyncAsync(enabled: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Shutdown. Nothing useful can be done, and a dialog on the way out
            // is worse than a hook that is cleaned up at the next start.
            _log.LogWarning(ex, "Could not remove the session hooks on shutdown.");
        }
    }

    /// <summary>Applies a new retention without restarting anything.</summary>
    public void SetRetention(TimeSpan retention) => _registry.SetRetention(retention, _clock.GetUtcNow());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _sweep?.Dispose();
            _sweep = null;
        }
    }

    private void OnSpoolChanged(object? sender, FileSystemEventArgs e)
    {
        // The file is moved into place, so it is complete the moment it appears -
        // but a burst of sessions finishing together produces a burst of events,
        // and draining once after a short pause reads them all in one pass.
        _ = SettleThenDrainAsync();
    }

    private async Task SettleThenDrainAsync()
    {
        try
        {
            await Task.Delay(Settle).ConfigureAwait(false);
            Drain();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Draining the session spool failed; the sweep will retry.");
        }
    }

    private void Drain()
    {
        if (_disposed)
        {
            return;
        }

        // Collected under the lock, raised outside it: a handler runs application
        // code, and holding a lock across that invites a deadlock for no reason.
        List<(SessionChange Change, ClaudeSession Session)> raised = [];

        lock (_drainGate)
        {
            Collect(raised);

            if (raised.Count > 0)
            {
                // Written on every change rather than at shutdown: the app can be
                // force-killed, and a list only saved on a clean exit is a list
                // that is lost exactly when losing it matters.
                _store.Save(_registry.Snapshot(_clock.GetUtcNow()));
            }
        }

        foreach ((SessionChange change, ClaudeSession session) in raised)
        {
            Changed?.Invoke(this, new SessionChangedEventArgs(change, session));
        }
    }

    private void Collect(List<(SessionChange Change, ClaudeSession Session)> raised)
    {
        DateTimeOffset now = _clock.GetUtcNow();

        foreach (SessionEvent report in _spool.Drain(now))
        {
            SessionChange change = _registry.Apply(report);

            // Every event, including the ones that change nothing. Without this
            // there is no way to tell "Claude Code never ran the hook" from "the
            // hook ran and we ignored it", and those have completely different
            // causes - the first is a settings file that was not reloaded, the
            // second is ours.
            _log.LogInformation(
                "Session {Kind} for {Folder} ({Id}) -> {Change}.",
                report.Kind,
                report.Folder,
                report.Id,
                change);

            // Raised even when the change is None. "Nothing worth announcing" is
            // not "nothing happened": a submitted prompt puts a session into
            // working, which is exactly the transition the list exists to show.
            // Filtering it out here is what left the row saying "waiting for you"
            // for the whole time the session was busy - the registry knew, and
            // nothing ever asked it. Whether to interrupt the user is the
            // controller's decision, further down.
            if (_registry.Find(report.Id) is not { } session)
            {
                continue;
            }

            raised.Add((change, session));
        }
    }
}
