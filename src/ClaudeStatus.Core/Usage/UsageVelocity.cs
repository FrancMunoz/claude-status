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
/// The nominal length of each limit window.
/// </summary>
/// <remarks>
/// An assumption about the plan, not a fact from the endpoint: the source says
/// when a window resets and never how long it is. Kept in one place so the
/// widget's elapsed bar and the velocity rule cannot disagree about how long a
/// week is, and used only where a *length* is genuinely needed - every countdown
/// and every alert still works from <see cref="UsageWindow.ResetsAt"/> alone.
/// </remarks>
public static class UsageWindowSpans
{
    /// <summary>The rolling session window.</summary>
    public static readonly TimeSpan Session = TimeSpan.FromHours(5);

    /// <summary>The weekly windows, all-models and Fable alike.</summary>
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);
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
/// A measured climb: how fast, over how long, and how much like a trend it looks.
/// </summary>
/// <param name="PercentPerHour">The least-squares slope through the samples.</param>
/// <param name="Fit">
/// Coefficient of determination, 0 to 1. How much of the movement the straight
/// line actually explains - 1 is a perfectly steady climb, near 0 is a flat line
/// with one jump in it.
/// </param>
/// <param name="Span">The time the samples cover.</param>
/// <param name="Count">How many samples went into it.</param>
public readonly record struct UsageTrend(double PercentPerHour, double Fit, TimeSpan Span, int Count);

/// <summary>
/// The last little while of readings for one window, so a rate can be measured.
/// </summary>
/// <remarks>
/// Keeps only what <see cref="VelocityRule"/> looks at - samples younger than
/// <see cref="Horizon"/> - and only fresh ones: a cached, stale reading has a
/// timestamp from before the app started and would make any rate meaningless.
/// The horizon is per-instance because the windows are not alike: half an hour
/// is a tenth of a session and a six-hundredth of a week, and a rate measured
/// over a six-hundredth of a window is a rumour.
/// </remarks>
public sealed class UsageHistory
{
    /// <summary>How far back a rate is measured over, unless told otherwise.</summary>
    public static readonly TimeSpan DefaultHorizon = TimeSpan.FromMinutes(30);

    private readonly List<UsageSample> _samples = [];

    /// <summary>A history that measures over <see cref="DefaultHorizon"/>.</summary>
    public UsageHistory()
        : this(DefaultHorizon)
    {
    }

    /// <param name="horizon">
    /// How far back to keep samples. Clamped up to <see cref="VelocityRule.MinimumSpan"/>,
    /// below which no rate can be measured at all and the history would be pointless.
    /// </param>
    public UsageHistory(TimeSpan horizon)
    {
        Horizon = horizon < VelocityRule.MinimumSpan ? VelocityRule.MinimumSpan : horizon;
    }

    /// <summary>How far back this history keeps samples.</summary>
    public TimeSpan Horizon { get; }

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
/// The projection alone says yes far too often, so five things have to agree
/// before it is allowed to speak:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Enough to measure.</b> At least <see cref="MinimumSamples"/> readings
/// covering at least <see cref="MinimumSpan"/>, because two polls a minute apart
/// can show a jump that is really one long request landing.
/// </item>
/// <item>
/// <b>It has to look like a climb.</b> The straight line through the samples must
/// explain at least <see cref="MinimumFit"/> of their movement. A flat half hour
/// with one spike at the end has the same endpoints as a steady climb and means
/// something completely different; the slope cannot tell them apart and this can.
/// </item>
/// <item>
/// <b>A floor on the rate.</b> <see cref="MinimumPercentPerHour"/>, because a
/// window at 99 % "exhausts before reset" at any positive rate and that is not
/// news.
/// </item>
/// <item>
/// <b>No wild extrapolation.</b> The wall has to be within
/// <see cref="MaximumProjection"/> times the span actually measured. Half an hour
/// of samples can support a guess about the next few hours; it cannot support one
/// about next Thursday, and on a seven-day window the unguarded projection was
/// happy to make it.
/// </item>
/// <item>
/// <b>Corroboration from the whole window.</b> Where the window's nominal length
/// is known, the spend so far must also be ahead of the clock - more of the
/// budget gone than of the window. The recent rate and the long-run average are
/// independent evidence, and requiring both is what separates "this pace will
/// cost you the window" from "you had a busy twenty minutes".
/// </item>
/// </list>
/// <para>
/// Once an alert is up, <c>latched</c> relaxes the two soft gates so a pace
/// hovering at the threshold does not make the warning blink on and off between
/// polls. The hard gates - exhausted, unknown reset, beats the reset - never
/// relax.
/// </para>
/// </remarks>
public static class VelocityRule
{
    /// <summary>Least time the samples must cover before a rate means anything.</summary>
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(4);

    /// <summary>Fewest readings a rate may be measured from.</summary>
    /// <remarks>
    /// Two points always fit a line perfectly, so the fit gate cannot see anything
    /// with fewer than three; four is the first count at which one outlier is
    /// outvoted. Polls are a minute apart, so <see cref="MinimumSpan"/> normally
    /// supplies more than this anyway - it bites only just after a reset.
    /// </remarks>
    public const int MinimumSamples = 4;

    /// <summary>Slowest climb that can raise an alert, in percent per hour.</summary>
    public const double MinimumPercentPerHour = 5d;

    /// <summary>How straight the climb has to look, as a coefficient of determination.</summary>
    public const double MinimumFit = 0.6d;

    /// <summary>How far ahead a measurement may be projected, as a multiple of its own span.</summary>
    public const double MaximumProjection = 8d;

    /// <summary>How much the soft gates relax while an alert is already up.</summary>
    public const double LatchRelief = 0.6d;

    /// <summary>Measures the climb over the samples, in percent per hour. Null when too few or too close.</summary>
    public static double? Rate(IReadOnlyList<UsageSample> samples) => Measure(samples)?.PercentPerHour;

    /// <summary>
    /// Fits a straight line to the samples.
    /// </summary>
    /// <param name="samples">Readings, oldest first.</param>
    /// <returns>
    /// The slope, its fit and what it was measured over; null when there is too
    /// little to measure - fewer than <see cref="MinimumSamples"/> readings, or
    /// less than <see cref="MinimumSpan"/> between the first and the last.
    /// </returns>
    /// <remarks>
    /// Least squares over every sample, not the first and the last. The endpoint
    /// rate this replaced threw away the twenty-eight readings in between, so one
    /// burst at either end set the whole slope and an idle half hour with a spike
    /// in it was indistinguishable from a steady climb.
    /// </remarks>
    public static UsageTrend? Measure(IReadOnlyList<UsageSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count < MinimumSamples)
        {
            return null;
        }

        TimeSpan span = samples[^1].At - samples[0].At;
        if (span < MinimumSpan)
        {
            return null;
        }

        // Hours since the first sample, so the slope comes out in percent per hour
        // and the numbers stay small enough not to lose precision.
        DateTimeOffset origin = samples[0].At;
        double meanT = 0d;
        double meanP = 0d;
        foreach (UsageSample s in samples)
        {
            meanT += (s.At - origin).TotalHours;
            meanP += s.Percent;
        }

        meanT /= samples.Count;
        meanP /= samples.Count;

        double covariance = 0d;
        double varianceT = 0d;
        double varianceP = 0d;
        foreach (UsageSample s in samples)
        {
            double dt = (s.At - origin).TotalHours - meanT;
            double dp = s.Percent - meanP;
            covariance += dt * dp;
            varianceT += dt * dt;
            varianceP += dp * dp;
        }

        if (varianceT <= 0d)
        {
            // Every sample at the same instant. The span check above should have
            // caught it; this is the arithmetic guard, not the policy.
            return null;
        }

        double slope = covariance / varianceT;

        // A window that did not move at all is a perfect flat line, and calling
        // that a bad fit would be backwards - it is an excellent fit to a slope of
        // zero, which the rate floor then rejects on its own terms.
        double fit = varianceP <= 0d ? 1d : Math.Clamp((covariance * covariance) / (varianceT * varianceP), 0d, 1d);

        return new UsageTrend(slope, fit, span, samples.Count);
    }

    /// <summary>
    /// Projects the current pace against the window's reset.
    /// </summary>
    /// <param name="window">Which limit this is, for the alert.</param>
    /// <param name="samples">Recent fresh readings, oldest first.</param>
    /// <param name="resetsAt">When the window resets; null means unknown, and no alert.</param>
    /// <param name="now">The current time.</param>
    /// <param name="span">
    /// The window's nominal length, which enables the budget-pace check. Null
    /// skips that check rather than guessing a length.
    /// </param>
    /// <param name="latched">
    /// True when this window is already alerting, which relaxes the rate floor and
    /// the fit gate so a borderline pace does not flicker.
    /// </param>
    /// <returns>An alert when the window would be exhausted before it resets; otherwise null.</returns>
    public static VelocityAlert? Evaluate(
        VelocityWindow window,
        IReadOnlyList<UsageSample> samples,
        DateTimeOffset? resetsAt,
        DateTimeOffset now,
        TimeSpan? span = null,
        bool latched = false)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (resetsAt is null || Measure(samples) is not { } trend)
        {
            return null;
        }

        double relief = latched ? LatchRelief : 1d;
        if (trend.PercentPerHour < MinimumPercentPerHour * relief || trend.Fit < MinimumFit * relief)
        {
            return null;
        }

        double percent = samples[^1].Percent;
        if (IndicatorText.IsExhausted(percent))
        {
            // Already out. The icon and the widget show that; a velocity alert
            // would be telling the user about a cliff they have gone over.
            // "Spent" is the reading the indicators call spent, not a bare 100,
            // so the warning cannot linger beside a display already showing 100 %.
            return null;
        }

        TimeSpan untilReset = resetsAt.Value - now;
        if (untilReset <= TimeSpan.Zero)
        {
            return null;
        }

        TimeSpan untilExhausted = TimeSpan.FromHours((100d - percent) / trend.PercentPerHour);
        if (untilExhausted >= untilReset)
        {
            return null;
        }

        // Do not project further ahead than the measurement can carry.
        if (untilExhausted > trend.Span * MaximumProjection)
        {
            return null;
        }

        // And the window as a whole has to agree: more of the allowance spent than
        // of the time it covers.
        if (span is { } length && length > TimeSpan.Zero && !IsAheadOfBudget(percent, untilReset, length))
        {
            return null;
        }

        return new VelocityAlert(window, trend.PercentPerHour, untilExhausted, untilReset);
    }

    /// <summary>
    /// Whether more of the window's allowance has gone than of the window itself.
    /// </summary>
    /// <remarks>
    /// The same comparison the widget draws as a second bar under the first: spend
    /// against clock. A window barely started has no useful budget line - everything
    /// is ahead of nothing - so the elapsed share carries the answer on its own there.
    /// </remarks>
    private static bool IsAheadOfBudget(double percent, TimeSpan untilReset, TimeSpan span)
    {
        TimeSpan elapsed = span - untilReset;
        if (elapsed <= TimeSpan.Zero)
        {
            return true;
        }

        double budget = 100d * (elapsed.TotalHours / span.TotalHours);
        return percent > budget;
    }
}

/// <summary>
/// Watches the snapshots go by and says when to warn about the pace.
/// </summary>
/// <remarks>
/// <para>
/// The per-window histories, the choice of horizon, which window's warning wins
/// and the latch that keeps a borderline alert from blinking all live here rather
/// than in the tray controller, so the whole decision can be tested without a
/// window on screen. The controller is left with the part that is genuinely about
/// the UI: wording the sentence and deciding how often to flash it.
/// </para>
/// <para>
/// Two readings are never worth an alert, and neither is a stale one: a cached
/// number carries a timestamp from before the app started, and a rate measured
/// against it is about a different day. A stale snapshot therefore leaves
/// <see cref="Current"/> exactly as it was - the pace has not been disproved,
/// it has simply not been observed.
/// </para>
/// </remarks>
public sealed class VelocityTracker
{
    /// <summary>How far back the session's rate is measured.</summary>
    /// <remarks>Half an hour is a tenth of a five-hour window: a real lever.</remarks>
    public static readonly TimeSpan SessionHorizon = TimeSpan.FromMinutes(30);

    /// <summary>How far back the week's rate is measured.</summary>
    /// <remarks>
    /// Hours, not minutes. A week judged on half an hour projects any busy patch
    /// into a catastrophe; three hours is long enough for the slope to mean
    /// something, and long enough that <see cref="VelocityRule.MaximumProjection"/>
    /// then permits a projection a day out - which is the scale a weekly limit
    /// actually runs out on.
    /// </remarks>
    public static readonly TimeSpan WeekHorizon = TimeSpan.FromHours(3);

    private readonly UsageHistory _session = new(SessionHorizon);
    private readonly UsageHistory _week = new(WeekHorizon);

    /// <summary>The alert in force, or null when the pace is fine.</summary>
    public VelocityAlert? Current { get; private set; }

    /// <summary>The session's retained readings, oldest first.</summary>
    public IReadOnlyList<UsageSample> SessionSamples => _session.Samples;

    /// <summary>The week's retained readings, oldest first.</summary>
    public IReadOnlyList<UsageSample> WeekSamples => _week.Samples;

    /// <summary>
    /// Records a snapshot and re-decides whether to warn.
    /// </summary>
    /// <param name="snapshot">The reading. Null or stale records nothing.</param>
    /// <param name="now">The current time.</param>
    /// <param name="enabled">
    /// Whether the user wants these warnings. Samples are recorded either way, so
    /// turning them back on does not start from no history.
    /// </param>
    /// <returns><see cref="Current"/> after the update.</returns>
    public VelocityAlert? Observe(UsageSnapshot? snapshot, DateTimeOffset now, bool enabled)
    {
        if (snapshot is null || snapshot.IsStale)
        {
            return Current;
        }

        // Any limit at 100 % means the user is already cut off, so nothing can be
        // climbing towards anything: a window that still projects a fast pace is
        // measuring spend that can no longer happen. Samples are still recorded -
        // the rate has to be there when the window resets - but nothing is said.
        bool exhausted = IsExhausted(snapshot.Session)
            || IsExhausted(snapshot.Week)
            || IsExhausted(snapshot.WeekFable);

        VelocityAlert? worst = null;
        foreach ((VelocityWindow kind, UsageWindow? window, UsageHistory history) in Windows(snapshot))
        {
            if (window is null)
            {
                history.Clear();
                continue;
            }

            history.Add(new UsageSample(snapshot.FetchedAt, window.Percent));

            if (!enabled || exhausted)
            {
                continue;
            }

            VelocityAlert? alert = VelocityRule.Evaluate(
                kind,
                history.Samples,
                window.ResetsAt,
                now,

                // Only the week is asked to corroborate. Its rate is measured over
                // 1.8 % of the window, so the long-run average is the thing that
                // decides whether a fast three hours matters. The session's is
                // measured over a tenth of a five-hour window and projected inside
                // that same window, which needs no second opinion - and asking for
                // one would silence exactly the case the alert exists for: a burst
                // that eats an hour of allowance in ten minutes is still under the
                // budget line at the moment it happens.
                kind == VelocityWindow.Week ? UsageWindowSpans.Week : null,
                latched: Current?.Window == kind);

            // Keep the one that runs out soonest, the most urgent thing to say.
            if (alert is not null && (worst is null || alert.UntilExhausted < worst.UntilExhausted))
            {
                worst = alert;
            }
        }

        Current = worst;
        return Current;
    }

    /// <summary>Forgets every reading and drops any alert.</summary>
    public void Reset()
    {
        _session.Clear();
        _week.Clear();
        Current = null;
    }

    private (VelocityWindow Kind, UsageWindow? Window, UsageHistory History)[] Windows(UsageSnapshot snapshot)
        =>
        [
            (VelocityWindow.Session, snapshot.Session, _session),
            (VelocityWindow.Week, snapshot.Week, _week),
        ];

    private static bool IsExhausted(UsageWindow? window)
        => window is not null && IndicatorText.IsExhausted(window.Percent);
}
