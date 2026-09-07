namespace ClaudeStatus.Usage;

/// <summary>
/// Decides when a reading is old enough to be shown as out of date.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UsageSnapshot.IsStale"/> answers "did the last refresh work", and
/// <see cref="UsageMonitor"/> sets it the moment one request fails. That is the
/// right flag for the monitor and the wrong one to paint with: this endpoint 429s
/// readily, so on that rule alone an indicator spends much of its time faded while
/// showing a number that arrived seconds ago.
/// </para>
/// <para>
/// The question a reader is actually asking is "can I still trust this number",
/// and that is about age. Both conditions are required: the flag on its own fades
/// a fresh reading after one hiccup, and age on its own would fade a perfectly
/// good one in the gap between a slow poll and its reply.
/// </para>
/// <para>
/// Shared by every indicator on every platform - the Windows icon and widget, the
/// Linux tray, the macOS menu bar - so a reading that looks doubtful on one
/// machine looks doubtful on all of them.
/// </para>
/// <para>
/// This governs <b>appearance</b> only. The worded explanations elsewhere - the
/// popup's banner, the report's freshness line - still follow the raw flag,
/// because "the last refresh failed" is a true and useful sentence however recent
/// the reading is. Fading says "do not trust this"; those say what happened.
/// </para>
/// </remarks>
public static class StalePolicy
{
    /// <summary>
    /// The shortest a reading may be considered current for.
    /// </summary>
    /// <remarks>
    /// Covers a very short configured poll interval. A reading is not really
    /// doubtful a minute in, whatever the interval says, and fading that fast
    /// would make the indicator flicker between states on a flaky connection.
    /// </remarks>
    public static readonly TimeSpan Floor = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How many poll intervals a reading may age before it is shown as old.
    /// </summary>
    /// <remarks>
    /// Three is long enough that one failed poll and its retry pass unnoticed -
    /// which is the case this exists for - and short enough that a real outage
    /// shows within a few minutes.
    /// </remarks>
    public const int PollIntervalMultiple = 3;

    /// <summary>How old a reading may be before it is shown as out of date.</summary>
    /// <param name="pollInterval">
    /// How often a reading is expected. Null, zero or negative falls back to
    /// <see cref="Floor"/> rather than fading everything immediately.
    /// </param>
    public static TimeSpan ThresholdFor(TimeSpan? pollInterval)
    {
        if (pollInterval is not { } interval || interval <= TimeSpan.Zero)
        {
            return Floor;
        }

        TimeSpan scaled = interval * PollIntervalMultiple;
        return scaled > Floor ? scaled : Floor;
    }

    /// <summary>Whether an indicator should draw this reading as out of date.</summary>
    /// <param name="snapshot">The reading, or null when there is none yet.</param>
    /// <param name="now">The current time, from the caller's clock.</param>
    /// <param name="threshold">From <see cref="ThresholdFor"/>.</param>
    public static bool ShowsAsStale(UsageSnapshot? snapshot, DateTimeOffset now, TimeSpan threshold)
        => snapshot is { IsStale: true } && snapshot.Age(now) > threshold;
}
