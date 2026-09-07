namespace ClaudeStatus.Usage;

/// <summary>
/// One usage window as reported by the source: how much of it is consumed, and
/// when it resets.
/// </summary>
/// <param name="Percent">
/// Consumption as 0-100. The endpoint returns this as an int on some fields and
/// a float on others, so it is always widened to <see cref="double"/>.
/// Values outside 0-100 are clamped by <see cref="Create"/> rather than trusted.
/// </param>
/// <param name="ResetsAt">
/// When the window rolls over, or <c>null</c> when the source does not say.
/// A null reset is normal - do not treat it as an error. Never assume the
/// session window is five hours; read this instead.
/// </param>
/// <remarks>
/// <para>
/// The two positional members are what the tray icon and the details popup need.
/// Everything below them is reported by the endpoint but not needed to answer
/// "how much have I used", so it is optional, defaulted, and shown only in the
/// report window. Keeping them as <c>init</c> properties rather than positional
/// parameters means <see cref="Create"/> and every existing call site still
/// compile, and record equality still covers them.
/// </para>
/// <para>
/// The dollar figures were <c>null</c> on every window of the Max-plan capture in
/// <c>tests/ClaudeStatus.Core.Tests/Fixtures/usage-normal.json</c>. They are read
/// anyway, because the alternative is discovering they exist only when a user
/// asks why the app shows less than claude.ai does.
/// </para>
/// </remarks>
public sealed record UsageWindow(double Percent, DateTimeOffset? ResetsAt)
{
    /// <summary>
    /// The source's own opinion of this window, e.g. <c>normal</c> or <c>warning</c>.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately a raw string, never an enum.</b> The set is undocumented
    /// and open; binding it would turn a new value into a parse failure rather
    /// than an unfamiliar label. Compare with <see cref="IsWarning"/> if you need
    /// a decision, and show the raw text if you only need to display it.
    /// </remarks>
    public string? Severity { get; init; }

    /// <summary>
    /// The <c>kind</c> the source gave, e.g. <c>session</c>, <c>weekly_all</c>,
    /// <c>weekly_scoped</c>. Raw, for the same reason as <see cref="Severity"/>.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    /// The model this window is scoped to, when it is scoped to one.
    /// </summary>
    public string? ScopeModel { get; init; }

    /// <summary>
    /// Whether the source considers this window the one currently being consumed.
    /// </summary>
    /// <remarks>
    /// Undocumented. In the recorded capture exactly one window had it set, and it
    /// was not the one with the highest percentage, so it is shown as information
    /// and nothing is decided by it.
    /// </remarks>
    public bool IsActive { get; init; }

    /// <summary>Spend cap for this window in dollars, when the plan has one.</summary>
    public double? LimitDollars { get; init; }

    /// <summary>Dollars consumed in this window, when the source reports them.</summary>
    public double? UsedDollars { get; init; }

    /// <summary>Dollars left in this window, when the source reports them.</summary>
    public double? RemainingDollars { get; init; }

    /// <summary>Why this window is unavailable, when the source says it is.</summary>
    public string? LockedReason { get; init; }

    /// <summary>True when the source labelled this window as anything but normal.</summary>
    /// <remarks>
    /// The check is "not normal" rather than "is warning", so a severity we have
    /// never seen before is treated as worth noticing rather than ignored.
    /// </remarks>
    public bool IsWarning => Severity is { Length: > 0 }
        && !string.Equals(Severity, "normal", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds a window, clamping the percentage into 0-100.</summary>
    public static UsageWindow Create(double percent, DateTimeOffset? resetsAt)
        => new(double.IsFinite(percent) ? Math.Clamp(percent, 0d, 100d) : 0d, resetsAt);

    /// <summary>
    /// How long until this window resets, relative to <paramref name="now"/>.
    /// Null when the source gave no reset time; <see cref="TimeSpan.Zero"/> once
    /// the reset time has passed (the source has simply not caught up yet).
    /// </summary>
    public TimeSpan? TimeUntilReset(DateTimeOffset now)
        => ResetsAt is null ? null
         : ResetsAt.Value <= now ? TimeSpan.Zero
         : ResetsAt.Value - now;
}
