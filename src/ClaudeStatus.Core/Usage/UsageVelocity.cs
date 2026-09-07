namespace ClaudeStatus.Usage;

/// <summary>One reading of one window: when it was taken and how full the window was.</summary>
public readonly record struct UsageSample(DateTimeOffset At, double Percent);

/// <summary>
/// Which limit a <see cref="VelocityAlert"/> is about.
/// </summary>
public enum VelocityWindow
{
    Session = 0,

    Week = 1,
}

/// <summary>
/// Usage is climbing fast enough to exhaust a window before it resets.
/// </summary>
/// <param name="Window">Which limit.</param>
/// <param name="PercentPerHour">How fast it is climbing, measured over the recent samples.</param>
/// <param name="UntilExhausted">How long until it hits 100 % at this pace.</param>
/// <param name="UntilReset">How long until the window would have reset anyway.</param>
public sealed record VelocityAlert(
    VelocityWindow Window,
    double PercentPerHour,
    TimeSpan UntilExhausted,
    TimeSpan UntilReset);

/// <summary>
/// The last little while of readings for one window, so a rate can be measured.
/// </summary>
/// <remarks>
/// Keeps only what <see cref="VelocityRule"/> looks at - samples younger than
/// <see cref="Horizon"/> - and only fresh ones: a cached, stale reading has a
/// timestamp from before the app started and would make any rate meaningless.
/// </remarks>
public sealed class UsageHistory
{
    /// <summary>How far back a rate is measured over.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromMinutes(30);

    private readonly List<UsageSample> _samples = [];

    /// <summary>The retained samples, oldest first.</summary>
    public IReadOnlyList<UsageSample> Samples => _samples;

    /// <summary>Records a reading and forgets anything older than the horizon.</summary>
    public void Add(UsageSample sample)
    {
        // A reset shows up as the percentage falling. Everything before it is
        // about a window that no longer exists, so it goes.
        if (_samples.Count > 0 && sample.Percent < _samples[^1].Percent)
        {
            _samples.Clear();
        }

        _samples.Add(sample);
        _samples.RemoveAll(s => sample.At - s.At > Horizon);
    }

    /// <summary>Forgets everything.</summary>
    public void Clear() => _samples.Clear();
}

/// <summary>
/// Decides whether usage is climbing too fast.
/// </summary>
/// <remarks>
/// <para>
/// "Too fast" is not a fixed rate. Twenty percent an hour is fine with four
/// hours left in a session that is at 10 %, and alarming at 60 % with an hour
/// to go. So the rule projects: at the rate measured over the recent samples,
/// does the window hit 100 % before it resets? If so the user is about to be
/// cut off, and that is the thing worth interrupting them for.
/// </para>
/// <para>
/// Two guards keep it quiet. The samples must span at least
/// <see cref="MinimumSpan"/>, because two polls a minute apart can show a jump
/// that is really one long request landing. And the rate must clear
/// <see cref="MinimumPercentPerHour"/>, because a window at 99 % "exhausts before
/// reset" at any positive rate and that is not news.
/// </para>
/// </remarks>
public static class VelocityRule
{
    /// <summary>Least time the samples must cover before a rate means anything.</summary>
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(4);

    /// <summary>Slowest climb that can raise an alert, in percent per hour.</summary>
    public const double MinimumPercentPerHour = 5d;

    /// <summary>Measures the climb over the samples, in percent per hour. Null when too few or too close.</summary>
    public static double? Rate(IReadOnlyList<UsageSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count < 2)
        {
            return null;
        }

        UsageSample first = samples[0];
        UsageSample last = samples[^1];
        TimeSpan span = last.At - first.At;
        if (span < MinimumSpan)
        {
            return null;
        }

        return (last.Percent - first.Percent) / span.TotalHours;
    }

    /// <summary>
    /// Projects the current pace against the window's reset.
    /// </summary>
    /// <param name="window">Which limit this is, for the alert.</param>
    /// <param name="samples">Recent fresh readings, oldest first.</param>
    /// <param name="resetsAt">When the window resets; null means unknown, and no alert.</param>
    /// <param name="now">The current time.</param>
    /// <returns>An alert when the window would be exhausted before it resets; otherwise null.</returns>
    public static VelocityAlert? Evaluate(
        VelocityWindow window,
        IReadOnlyList<UsageSample> samples,
        DateTimeOffset? resetsAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (resetsAt is null || Rate(samples) is not { } rate || rate < MinimumPercentPerHour)
        {
            return null;
        }

        double percent = samples[^1].Percent;
        if (percent >= 100d)
        {
            // Already out. The icon and the widget show that; a velocity alert
            // would be telling the user about a cliff they have gone over.
            return null;
        }

        TimeSpan untilReset = resetsAt.Value - now;
        if (untilReset <= TimeSpan.Zero)
        {
            return null;
        }

        TimeSpan untilExhausted = TimeSpan.FromHours((100d - percent) / rate);
        return untilExhausted < untilReset
            ? new VelocityAlert(window, rate, untilExhausted, untilReset)
            : null;
    }
}
