namespace ClaudeStatus.Export;

/// <summary>
/// What the app publishes to other software on the same machine: the reading the
/// indicators show, as data rather than as a rendered surface.
/// </summary>
/// <remarks>
/// <para>
/// The first consumer is the iCUE widget for the CORSAIR XENEON EDGE
/// (<c>widgets/icue</c>), which is a web page inside iCUE and cannot read the app's
/// windows. It polls <see cref="UsageExportServer"/> and draws this.
/// </para>
/// <para>
/// <b>Nothing here identifies the account.</b> Percentages, reset times, a pace
/// verdict and a session count are the whole of it: no token, no organisation id,
/// no spend, no session ids or titles. A page that can read this learns how busy
/// the user is and nothing else. See <c>docs/security.md</c> §5.
/// </para>
/// <para>
/// Language-neutral on purpose. Window ids and states are fixed identifiers, and
/// the pace verdict is numbers and times: the consumer words its own sentence, in
/// its own language, so the widget's translations do not depend on the app's.
/// </para>
/// <para>
/// <see cref="Schema"/> is bumped when a change would break a consumer written
/// against the previous shape. Adding a property is not such a change; consumers
/// are asked to tolerate unknown fields, as the app does with Anthropic's.
/// </para>
/// </remarks>
/// <param name="Schema">The contract version. See <see cref="CurrentSchema"/>.</param>
/// <param name="State">Whether there is a reading, and if not, why.</param>
/// <param name="AppVersion">The app's version, so a consumer can say what it is talking to.</param>
/// <param name="FetchedAt">When the reading was taken. Null without one.</param>
/// <param name="IsStale">Whether the reading is old enough that the indicators fade it.</param>
/// <param name="ThresholdPercent">The user's alert threshold, so the consumer turns red where the icon does.</param>
/// <param name="Windows">The limit windows, in the indicators' order. Empty without a reading.</param>
/// <param name="Pace">The velocity warning, or null while the pace is safe.</param>
/// <param name="Sessions">The Claude Code session count, or null while the watch is off.</param>
public sealed record UsageExport(
    int Schema,
    UsageExportState State,
    string AppVersion,
    DateTimeOffset? FetchedAt,
    bool IsStale,
    double ThresholdPercent,
    IReadOnlyList<UsageExportWindow> Windows,
    UsageExportPace? Pace,
    UsageExportSessions? Sessions)
{
    /// <summary>The schema this build writes.</summary>
    public const int CurrentSchema = 1;

    /// <summary>The id of the 5-hour window.</summary>
    public const string SessionWindowId = "session";

    /// <summary>The id of the 7-day, all-models window.</summary>
    public const string WeekWindowId = "week";

    /// <summary>The id of the 7-day, top-tier-models window.</summary>
    public const string WeekFableWindowId = "weekFable";
}

/// <summary>Why a reading may be missing. Mirrors what the indicators draw.</summary>
public enum UsageExportState
{
    /// <summary>A reading is present.</summary>
    Ok = 0,

    /// <summary>No credential: the app has nothing to fetch with. The icon shows <c>!</c>.</summary>
    NoCredential = 1,

    /// <summary>The endpoint could not be reached. The icon shows <c>⊘</c>; the windows carry the last reading, if any.</summary>
    Unreachable = 2,

    /// <summary>The app has started but not fetched yet. The icon shows <c>—</c>.</summary>
    NoData = 3,
}

/// <summary>One limit window.</summary>
/// <param name="Id">One of the <see cref="UsageExport"/> window ids.</param>
/// <param name="Percent">Used share, 0 to 100.</param>
/// <param name="ResetsAt">When the window resets, if the endpoint said.</param>
/// <param name="SpanSeconds">
/// The window's length, so a consumer can draw how much of it has elapsed. The
/// same assumption <see cref="Usage.UsageWindowSpans"/> makes, and the only one.
/// </param>
/// <param name="Exhausted">Whether the indicators call this window spent.</param>
public sealed record UsageExportWindow(
    string Id,
    double Percent,
    DateTimeOffset? ResetsAt,
    int? SpanSeconds,
    bool Exhausted);

/// <summary>The velocity tracker's verdict, as data.</summary>
/// <param name="WindowId">Which window is climbing too fast.</param>
/// <param name="PercentPerHour">The fitted rate.</param>
/// <param name="RunsOutAt">When the window reaches 100 % at this pace.</param>
/// <param name="ResetsAt">When it would have reset instead.</param>
public sealed record UsageExportPace(
    string WindowId,
    double PercentPerHour,
    DateTimeOffset RunsOutAt,
    DateTimeOffset ResetsAt);

/// <summary>How many Claude Code sessions the app can see.</summary>
/// <param name="Working">Sessions with a turn in progress.</param>
/// <param name="Open">Sessions that have not ended.</param>
public sealed record UsageExportSessions(int Working, int Open);
