namespace ClaudeStatus.Usage;

/// <summary>
/// A single reading of the account's usage.
/// </summary>
/// <remarks>
/// Every window is nullable. The upstream response is undocumented and has
/// already been observed to drop and rename fields between releases
/// (see <c>docs/data-source.md</c>), so "the source did not tell us" is a normal
/// state that the UI renders as a dash - never an error and never a zero.
/// </remarks>
/// <param name="Session">The rolling session window (commonly 5 h, but read <see cref="UsageWindow.ResetsAt"/>).</param>
/// <param name="Week">The 7-day all-models window.</param>
/// <param name="WeekFable">The 7-day window scoped to Fable. Null on plans that have no such limit.</param>
/// <param name="OtherWindows">
/// Any further scoped windows the source reported, keyed by model display name.
/// Unknown models land here instead of breaking the parse.
/// </param>
/// <param name="FetchedAt">When this snapshot was retrieved.</param>
/// <param name="IsStale">True when this is a cached reading being shown because a refresh failed.</param>
public sealed record UsageSnapshot(
    UsageWindow? Session,
    UsageWindow? Week,
    UsageWindow? WeekFable,
    IReadOnlyDictionary<string, UsageWindow> OtherWindows,
    DateTimeOffset FetchedAt,
    bool IsStale)
{
    /// <summary>The display name the source uses for the top-tier scoped window.</summary>
    public const string FableModelName = "Fable";

    /// <summary>
    /// Pay-as-you-go spending, when the source reported a <c>spend</c> block.
    /// </summary>
    /// <remarks>
    /// An <c>init</c> property rather than a positional parameter: it is shown only
    /// in the report window, and adding it to the constructor would churn every
    /// call site that builds a snapshot for a test.
    /// </remarks>
    public SpendInfo? Spend { get; init; }

    /// <summary>
    /// Whether the Fable window rolls over with the weekly one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True in two cases, and both mean the same thing to a reader:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     Both windows report effectively the same <c>resets_at</c>. Repeating an
    ///     identical countdown on two adjacent lines tells the user nothing.
    ///   </description></item>
    ///   <item><description>
    ///     The Fable window reports no reset time at all. Observed on a live
    ///     account sitting at 0 % Fable usage: the endpoint returns
    ///     <c>resets_at: null</c> for a scoped window it has nothing to say about.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>"Effectively" is doing real work there.</b> In the recorded capture the
    /// two stamps are 291 <i>microseconds</i> apart - <c>…59.968265</c> against
    /// <c>…59.968556</c> - because the server timestamps each limit as it builds
    /// the response rather than copying one value. An exact comparison therefore
    /// fails on the very data this exists for, which is how the first version of
    /// this property was caught.
    /// </para>
    /// <para>
    /// The second case is an inference rather than something the endpoint stated,
    /// and it is a safe one: the scoped weekly limit sits in the same <c>weekly</c>
    /// group as the all-models limit, so it cannot roll over on a different
    /// schedule. "Resets with the weekly limit" is both true and more useful than
    /// "reset time unknown", which reads like something is broken.
    /// </para>
    /// </remarks>
    public bool WeekFableSharesWeeklyReset =>
        WeekFable is not null
        && Week?.ResetsAt is { } weekly
        && (WeekFable.ResetsAt is not { } fable
            || (fable - weekly).Duration() <= SameResetTolerance);

    /// <summary>
    /// How far apart two reset times can be and still count as the same rollover.
    /// </summary>
    /// <remarks>
    /// A minute is enormous next to the sub-millisecond jitter actually observed,
    /// and tiny next to any genuinely separate schedule, which would differ by
    /// hours. Sizing it between the two means neither server-side noise nor a real
    /// divergence can be mistaken for the other.
    /// </remarks>
    private static readonly TimeSpan SameResetTolerance = TimeSpan.FromMinutes(1);

    /// <summary>Prepaid credits, when the source reported an <c>extra_usage</c> block.</summary>
    public ExtraUsageInfo? ExtraUsage { get; init; }

    /// <summary>
    /// Every window in the reading, in the order the report window shows them.
    /// </summary>
    /// <remarks>
    /// Labelled with a resource key for the three known windows and with the raw
    /// model name for anything scoped that we did not recognise, so a model added
    /// upstream appears under its own name rather than not at all.
    /// </remarks>
    public IEnumerable<(string Label, bool IsResourceKey, UsageWindow Window)> AllWindows()
    {
        if (Session is not null)
        {
            yield return ("Details_Metric_Session", true, Session);
        }

        if (Week is not null)
        {
            yield return ("Details_Metric_Week", true, Week);
        }

        if (WeekFable is not null)
        {
            yield return ("Details_Metric_WeekFable", true, WeekFable);
        }

        foreach (KeyValuePair<string, UsageWindow> entry in OtherWindows)
        {
            yield return (entry.Key, false, entry.Value);
        }
    }

    /// <summary>An empty snapshot, used before the first successful fetch.</summary>
    public static UsageSnapshot Empty(DateTimeOffset fetchedAt) => new(
        Session: null,
        Week: null,
        WeekFable: null,
        OtherWindows: new Dictionary<string, UsageWindow>(),
        FetchedAt: fetchedAt,
        IsStale: true);

    /// <summary>Returns the same reading marked stale, for display after a failed refresh.</summary>
    public UsageSnapshot AsStale() => IsStale ? this : this with { IsStale = true };

    /// <summary>Picks the window a given indicator mode displays.</summary>
    public UsageWindow? ForMode(IndicatorMode mode) => mode switch
    {
        IndicatorMode.SessionPercent => Session,
        IndicatorMode.WeekPercent => Week,
        IndicatorMode.WeekFablePercent => WeekFable,
        IndicatorMode.Ring => Session,

        // The row shows several, but callers that want one - the tooltip, the
        // exhausted check - get the session window, which is the one people act on.
        IndicatorMode.Row => Session,
        _ => null,
    };

    /// <summary>
    /// How old this reading is. Callers pass the clock so this stays testable.
    /// </summary>
    public TimeSpan Age(DateTimeOffset now)
    {
        TimeSpan age = now - FetchedAt;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    /// <summary>
    /// Value equality, including <see cref="OtherWindows"/>.
    /// </summary>
    /// <remarks>
    /// The compiler-generated record equality compares <see cref="OtherWindows"/>
    /// by reference, because <see cref="IReadOnlyDictionary{TKey,TValue}"/> does
    /// not implement structural equality. That would make two identical readings
    /// compare unequal, which matters: the app skips re-rendering the tray icon
    /// when a poll returns the same values, and reference equality would defeat
    /// that on every single poll.
    /// </remarks>
    public bool Equals(UsageSnapshot? other)
        => other is not null
        && Session == other.Session
        && Week == other.Week
        && WeekFable == other.WeekFable
        && FetchedAt == other.FetchedAt
        && IsStale == other.IsStale
        && Spend == other.Spend
        && ExtraUsage == other.ExtraUsage
        && SameOtherWindows(OtherWindows, other.OtherWindows);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(Session);
        hash.Add(Week);
        hash.Add(WeekFable);
        hash.Add(FetchedAt);
        hash.Add(IsStale);
        hash.Add(Spend);
        hash.Add(ExtraUsage);
        hash.Add(OtherWindows.Count);

        // Order-independent, so two dictionaries built in different orders agree.
        int windowsHash = 0;
        foreach (KeyValuePair<string, UsageWindow> entry in OtherWindows)
        {
            windowsHash ^= HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(entry.Key),
                entry.Value);
        }

        hash.Add(windowsHash);
        return hash.ToHashCode();
    }

    private static bool SameOtherWindows(
        IReadOnlyDictionary<string, UsageWindow> left,
        IReadOnlyDictionary<string, UsageWindow> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, UsageWindow> entry in left)
        {
            if (!right.TryGetValue(entry.Key, out UsageWindow? value) || value != entry.Value)
            {
                return false;
            }
        }

        return true;
    }
}
