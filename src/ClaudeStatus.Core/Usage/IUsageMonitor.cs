namespace ClaudeStatus.Usage;

/// <summary>What happened on the most recent fetch attempt.</summary>
/// <param name="Snapshot">The reading now on show, stale or not. Null before the first success.</param>
/// <param name="Failure">Why the last attempt failed, or null when it succeeded.</param>
/// <param name="NextPollDelay">How long the monitor will wait before trying again.</param>
public sealed record UsageMonitorStatus(
    UsageSnapshot? Snapshot,
    UsageFetchFailure? Failure,
    TimeSpan NextPollDelay)
{
    /// <summary>True when the last attempt succeeded.</summary>
    public bool IsHealthy => Failure is null;
}

/// <summary>
/// Polls an <see cref="IUsageProvider"/>, caches the last good reading, and
/// publishes snapshots to whoever is listening.
/// </summary>
public interface IUsageMonitor
{
    /// <summary>The most recent reading, stale or not. Null before the first success.</summary>
    UsageSnapshot? Latest { get; }

    /// <summary>The outcome of the last attempt.</summary>
    UsageMonitorStatus Status { get; }

    /// <summary>Fires on every snapshot, including one re-published as stale after a failure.</summary>
    IObservable<UsageSnapshot> Snapshots { get; }

    /// <summary>Begins polling. Idempotent.</summary>
    void Start();

    /// <summary>Stops polling. Idempotent; the cached snapshot survives.</summary>
    void Stop();

    /// <summary>
    /// Forces a fetch now, ignoring the schedule.
    /// </summary>
    /// <remarks>
    /// Client-side rate limited: calls inside the cooldown are ignored so a user
    /// leaning on the Refresh button cannot get us throttled. See
    /// <c>docs/data-source.md</c>.
    /// </remarks>
    /// <returns>True if a fetch actually ran; false if suppressed by the cooldown.</returns>
    Task<bool> RefreshNowAsync(CancellationToken ct = default);
}
