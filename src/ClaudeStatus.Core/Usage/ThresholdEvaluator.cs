namespace ClaudeStatus.Usage;

/// <summary>
/// Decides whether the metric on show has crossed the user's alert threshold.
/// </summary>
/// <remarks>
/// Only the metric currently displayed drives the icon colour - that is the
/// behaviour the UI spec asks for. <see cref="EvaluateAll"/> exists so the
/// details window can colour every bar independently.
/// </remarks>
public static class ThresholdEvaluator
{
    /// <summary>The default alert threshold, as a percentage.</summary>
    public const double DefaultThresholdPercent = 80d;

    /// <summary>Lowest threshold we let the user configure.</summary>
    public const double MinThresholdPercent = 1d;

    /// <summary>Highest threshold we let the user configure.</summary>
    public const double MaxThresholdPercent = 100d;

    /// <summary>
    /// Evaluates one window. A missing window is <see cref="ThresholdState.Unknown"/>,
    /// never <see cref="ThresholdState.Normal"/> - "we do not know" must not look calm.
    /// </summary>
    public static ThresholdState Evaluate(UsageWindow? window, double thresholdPercent)
        => window is null
            ? ThresholdState.Unknown
            : window.Percent >= Clamp(thresholdPercent) ? ThresholdState.Exceeded : ThresholdState.Normal;

    /// <summary>Evaluates the window that <paramref name="mode"/> displays.</summary>
    public static ThresholdState Evaluate(UsageSnapshot? snapshot, IndicatorMode mode, double thresholdPercent)
        => snapshot is null
            ? ThresholdState.Unknown
            : Evaluate(snapshot.ForMode(mode), thresholdPercent);

    /// <summary>
    /// Evaluates the row indicator: the worst verdict among the windows it shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row draws every window in one ink colour, so it needs one verdict. Worst
    /// wins, because the row turning red has to mean "something here is past your
    /// threshold" - taking the session window alone would leave a 92 % weekly limit
    /// looking calm.
    /// </para>
    /// <para>
    /// <paramref name="includeWeekFable"/> is not a formality. The row only draws
    /// the Fable window when the user asked for it, and judging a metric that is not
    /// on screen would turn the row red for a reading nobody can see.
    /// </para>
    /// </remarks>
    public static ThresholdState EvaluateRow(
        UsageSnapshot? snapshot, double thresholdPercent, bool includeWeekFable)
    {
        if (snapshot is null)
        {
            return ThresholdState.Unknown;
        }

        ThresholdState worst = Worst(
            Evaluate(snapshot.Session, thresholdPercent),
            Evaluate(snapshot.Week, thresholdPercent));

        return includeWeekFable && snapshot.WeekFable is not null
            ? Worst(worst, Evaluate(snapshot.WeekFable, thresholdPercent))
            : worst;
    }

    /// <summary>
    /// The more serious of two verdicts.
    /// </summary>
    /// <remarks>
    /// Exceeded beats Unknown beats Normal. Unknown outranking Normal keeps the
    /// promise <see cref="Evaluate(UsageWindow?, double)"/> makes: a window we know
    /// nothing about must never make the indicator look calm.
    /// </remarks>
    private static ThresholdState Worst(ThresholdState left, ThresholdState right)
    {
        if (left == ThresholdState.Exceeded || right == ThresholdState.Exceeded)
        {
            return ThresholdState.Exceeded;
        }

        return left == ThresholdState.Unknown || right == ThresholdState.Unknown
            ? ThresholdState.Unknown
            : ThresholdState.Normal;
    }

    /// <summary>Evaluates all three headline windows, for the details view.</summary>
    public static (ThresholdState Session, ThresholdState Week, ThresholdState WeekFable) EvaluateAll(
        UsageSnapshot? snapshot, double thresholdPercent)
        => snapshot is null
            ? (ThresholdState.Unknown, ThresholdState.Unknown, ThresholdState.Unknown)
            : (Evaluate(snapshot.Session, thresholdPercent),
               Evaluate(snapshot.Week, thresholdPercent),
               Evaluate(snapshot.WeekFable, thresholdPercent));

    /// <summary>Forces a configured threshold into the supported range.</summary>
    public static double Clamp(double thresholdPercent)
        => double.IsFinite(thresholdPercent)
            ? Math.Clamp(thresholdPercent, MinThresholdPercent, MaxThresholdPercent)
            : DefaultThresholdPercent;
}
