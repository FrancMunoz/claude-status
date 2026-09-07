namespace ClaudeStatus.Usage;

/// <summary>
/// The on-disk shape of a cached reading.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately flat and decoupled from <see cref="UsageSnapshot"/>. The domain
/// type is free to change shape; the file on disk should not break when it does,
/// and an old cache must degrade to "no cache" rather than crash a launch.
/// </para>
/// <para>
/// Internal rather than private-nested so the source-generated
/// <c>ClaudeStatusJsonContext</c> can reference it. Reflection-based
/// serialization is not an option here - it is silently removed by trimming and
/// AOT.
/// </para>
/// </remarks>
internal sealed record CachedSnapshot
{
    public int Version { get; init; } = 1;

    public DateTimeOffset FetchedAt { get; init; }

    public double? SessionPercent { get; init; }

    public DateTimeOffset? SessionResetsAt { get; init; }

    public double? WeekPercent { get; init; }

    public DateTimeOffset? WeekResetsAt { get; init; }

    public double? WeekFablePercent { get; init; }

    public DateTimeOffset? WeekFableResetsAt { get; init; }

    public static CachedSnapshot From(UsageSnapshot snapshot) => new()
    {
        FetchedAt = snapshot.FetchedAt,
        SessionPercent = snapshot.Session?.Percent,
        SessionResetsAt = snapshot.Session?.ResetsAt,
        WeekPercent = snapshot.Week?.Percent,
        WeekResetsAt = snapshot.Week?.ResetsAt,
        WeekFablePercent = snapshot.WeekFable?.Percent,
        WeekFableResetsAt = snapshot.WeekFable?.ResetsAt,
    };

    public UsageSnapshot ToSnapshot() => new(
        Session: SessionPercent is { } session ? UsageWindow.Create(session, SessionResetsAt) : null,
        Week: WeekPercent is { } week ? UsageWindow.Create(week, WeekResetsAt) : null,
        WeekFable: WeekFablePercent is { } fable ? UsageWindow.Create(fable, WeekFableResetsAt) : null,
        OtherWindows: new Dictionary<string, UsageWindow>(),
        FetchedAt: FetchedAt,

        // Always stale: it was fetched before this process existed.
        IsStale: true);
}
