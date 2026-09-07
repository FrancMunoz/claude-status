using ClaudeStatus.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeStatus.Usage;

/// <summary>
/// The polling loop: fetch, cache, publish, back off on failure.
/// </summary>
/// <remarks>
/// <para>
/// Timing goes through <see cref="TimeProvider"/> so tests drive it with a fake
/// clock instead of sleeping. Jitter is injected as a function for the same reason.
/// </para>
/// <para>
/// The loop must never let an exception escape. A tray app that dies because a
/// Wi-Fi hop failed is worse than one showing a stale number, so every failure
/// path ends in "re-publish the last reading, marked stale".
/// </para>
/// </remarks>
public sealed class UsageMonitor : IUsageMonitor, IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly TimeProvider _clock;
    private readonly Func<double> _jitter;
    private readonly ILogger<UsageMonitor> _log;
    private readonly SnapshotSubject _subject = new();
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly Lock _stateLock = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private UsageSnapshot? _latest;
    private UsageMonitorStatus _status;
    private int _consecutiveFailures;
    private DateTimeOffset _lastForcedRefresh = DateTimeOffset.MinValue;
    private bool _disposed;

    public UsageMonitor(
        IUsageProvider provider,
        PollingOptions? options = null,
        TimeProvider? clock = null,
        Func<double>? jitter = null,
        ILogger<UsageMonitor>? log = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Options = (options ?? new PollingOptions()).Normalized();
        _clock = clock ?? TimeProvider.System;
        _jitter = jitter ?? (() => Random.Shared.NextDouble());
        _log = log ?? NullLogger<UsageMonitor>.Instance;
        _status = new UsageMonitorStatus(null, null, Options.BaseInterval);
    }

    /// <summary>The timing policy in force.</summary>
    public PollingOptions Options { get; }

    /// <inheritdoc />
    public UsageSnapshot? Latest
    {
        get { lock (_stateLock) { return _latest; } }
    }

    /// <inheritdoc />
    public UsageMonitorStatus Status
    {
        get { lock (_stateLock) { return _status; } }
    }

    /// <inheritdoc />
    public IObservable<UsageSnapshot> Snapshots => _subject;

    /// <summary>True while the polling loop is running.</summary>
    public bool IsRunning
    {
        get { lock (_stateLock) { return _loopTask is not null; } }
    }

    /// <summary>Seeds the cache from disk so the tray shows something immediately at startup.</summary>
    public void SeedFrom(UsageSnapshot cached)
    {
        ArgumentNullException.ThrowIfNull(cached);
        UsageSnapshot stale = cached.AsStale();
        lock (_stateLock)
        {
            _latest ??= stale;
            _status = _status with { Snapshot = _latest };
        }

        _subject.Publish(stale);
    }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_loopTask is not null)
            {
                return;
            }

            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunAsync(_loopCts.Token));
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_stateLock)
        {
            if (_loopTask is null)
            {
                return;
            }

            cts = _loopCts;
            _loopCts = null;
            _loopTask = null;
        }

        cts?.Cancel();
        cts?.Dispose();
    }

    /// <inheritdoc />
    public async Task<bool> RefreshNowAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DateTimeOffset now = _clock.GetUtcNow();
        lock (_stateLock)
        {
            if (now - _lastForcedRefresh < Options.ForcedRefreshCooldown)
            {
                _log.LogDebug("Manual refresh suppressed: still inside the cooldown.");
                return false;
            }

            _lastForcedRefresh = now;
        }

        await FetchOnceAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The poll loop. Never throws; cancellation ends it quietly.</summary>
    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await FetchOnceAsync(ct).ConfigureAwait(false);

                TimeSpan delay = Status.NextPollDelay;
                await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() was called. Normal.
        }
        catch (Exception ex)
        {
            // Belt and braces: FetchOnceAsync already swallows everything, so
            // reaching here means a bug. Log it rather than tearing the app down.
            _log.LogError(ex, "The usage polling loop stopped unexpectedly.");
        }
    }

    /// <summary>
    /// One fetch attempt. Swallows every provider failure and turns it into a
    /// stale re-publish plus a longer next delay.
    /// </summary>
    private async Task FetchOnceAsync(CancellationToken ct)
    {
        // Serialised so a manual refresh cannot race the timer into a double call.
        if (!await _fetchGate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            _log.LogDebug("Fetch skipped: another fetch is already in flight.");
            return;
        }

        try
        {
            UsageSnapshot snapshot = await _provider.FetchAsync(ct).ConfigureAwait(false);

            lock (_stateLock)
            {
                _consecutiveFailures = 0;
                _latest = snapshot;
                _status = new UsageMonitorStatus(snapshot, null, Options.BaseInterval);
            }

            _subject.Publish(snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            UsageFetchFailure failure = ex is UsageFetchException fetch
                ? fetch.Failure
                : UsageFetchFailure.Network;
            TimeSpan? serverRetryAfter = (ex as UsageFetchException)?.RetryAfter;

            UsageSnapshot? stale;
            TimeSpan delay;
            lock (_stateLock)
            {
                _consecutiveFailures++;
                delay = NextDelayAfterFailure(_consecutiveFailures, serverRetryAfter);
                _latest = _latest?.AsStale();
                stale = _latest;
                _status = new UsageMonitorStatus(stale, failure, delay);
            }

            // Message only, never the exception's inner payload at Warning level -
            // a provider could in principle surface response text.
            _log.LogWarning(
                "Usage fetch failed ({Failure}); attempt {Attempt}. Next try in {Delay}.",
                failure,
                _consecutiveFailures,
                delay);

            if (stale is not null)
            {
                _subject.Publish(stale);
            }
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    /// <summary>
    /// Exponential backoff with jitter, honouring the server's Retry-After when
    /// it gave one.
    /// </summary>
    /// <remarks>
    /// Jitter is full-spectrum over the computed step, which keeps many installs
    /// from lining up on the same second after a shared outage.
    /// </remarks>
    internal TimeSpan NextDelayAfterFailure(int consecutiveFailures, TimeSpan? serverRetryAfter)
    {
        double multiplier = Math.Pow(2, Math.Min(consecutiveFailures - 1, 16));
        double seconds = Options.BaseInterval.TotalSeconds * multiplier;
        seconds = Math.Min(seconds, Options.MaxInterval.TotalSeconds);

        // Jitter spreads clients out; it only ever shortens within the band above base.
        double jittered = seconds * (1d - (Options.JitterFraction * _jitter()));
        jittered = Math.Max(jittered, Options.BaseInterval.TotalSeconds);

        TimeSpan delay = TimeSpan.FromSeconds(jittered);

        if (serverRetryAfter is { } retryAfter && retryAfter > delay)
        {
            // The server knows better than our curve, but we still cap it so a
            // silly Retry-After cannot park the app for an hour.
            delay = retryAfter > Options.MaxRetryAfter ? Options.MaxRetryAfter : retryAfter;
        }

        return delay;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _subject.Complete();
        _fetchGate.Dispose();
    }
}
