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
