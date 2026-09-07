using ClaudeStatus.Localization;
using ClaudeStatus.Usage;

namespace ClaudeStatus.App.ViewModels;

/// <summary>
/// One usage window as a row in the report window.
/// </summary>
/// <remarks>
/// Everything is pre-formatted into strings here rather than in the view, because
/// each line needs a translated template and a culture-aware number, and neither
/// belongs in AXAML. Rows are rebuilt rather than mutated on each reading, so
/// there is no partial-update state to get wrong.
/// </remarks>
public sealed class ReportRowViewModel
{
    /// <param name="localizer">Used at construction only; rows are rebuilt on a language change.</param>
    /// <param name="label">The metric name, already resolved.</param>
    /// <param name="window">The window this row describes.</param>
    /// <param name="now">The clock reading the countdown is relative to.</param>
    /// <param name="sharesWeeklyReset">
    /// True when this window rolls over with the weekly one. Only the countdown
    /// changes: <see cref="ResetsAtText"/> keeps reporting exactly what the
    /// endpoint said, because this window's job is to show the raw figures.
    /// </param>
    public ReportRowViewModel(
        ILocalizer localizer,
        string label,
        UsageWindow window,
        DateTimeOffset now,
        bool sharesWeeklyReset = false)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(window);

        // The field captions live on the row rather than being reached from the
        // template with a $parent binding. A row is rebuilt on every reading and
        // on every language change anyway, so there is nothing to keep in sync,
        // and the template stays readable.
        KindLabel = localizer["Report_Field_Kind"];
        SeverityLabel = localizer["Report_Field_Severity"];
        ActiveLabel = localizer["Report_Field_Active"];

        Label = label;
        Percent = window.Percent;
        PercentText = localizer.Format("Bar_Percent", window.Percent);
        ResetText = sharesWeeklyReset
            ? localizer["Reset_WithWeekly"]
            : UsageBarViewModel.FormatReset(localizer, window.TimeUntilReset(now));
        IsWarning = window.IsWarning;
        IsActive = window.IsActive;

        // The raw severity and kind are shown verbatim. They are open string sets
        // from an undocumented endpoint, and inventing a translation for a value
        // we have never seen would be worse than showing what arrived.
        Severity = window.Severity ?? localizer["Common_Unknown"];
        Kind = window.Kind ?? localizer["Common_Unknown"];

        ResetsAtText = window.ResetsAt is { } resetsAt
            ? resetsAt.ToLocalTime().ToString("f", localizer.Culture)
            : localizer["Report_NoResetTime"];

        ActiveText = localizer[window.IsActive ? "Report_Active_Yes" : "Report_Active_No"];

        // Every dollar figure was null in the recorded capture. The row simply
        // omits the line rather than showing "$ —", which reads as a failure.
        DollarsText = FormatDollars(localizer, window);
        HasDollars = DollarsText.Length > 0;

        LockedReason = window.LockedReason ?? string.Empty;
        IsLocked = LockedReason.Length > 0;

        HasScope = window.ScopeModel is { Length: > 0 };
        ScopeModel = HasScope ? localizer.Format("Report_Scope", window.ScopeModel!) : string.Empty;
    }

    /// <summary>Caption for the kind line.</summary>
    public string KindLabel { get; }

    /// <summary>Caption for the severity line.</summary>
    public string SeverityLabel { get; }

    /// <summary>Caption for the "currently consuming" line.</summary>
    public string ActiveLabel { get; }

    /// <summary>The metric name.</summary>
    public string Label { get; }

    /// <summary>Consumption, 0-100, for the bar.</summary>
    public double Percent { get; }

    /// <summary>Consumption as text, e.g. "43,5 %".</summary>
    public string PercentText { get; }

    /// <summary>"resets in 2h 37m", or why we cannot say.</summary>
    public string ResetText { get; }

    /// <summary>The exact reset time in the viewer's local zone.</summary>
    public string ResetsAtText { get; }

    /// <summary>The source's own severity label, verbatim.</summary>
    public string Severity { get; }

    /// <summary>The source's own kind, verbatim.</summary>
    public string Kind { get; }

    /// <summary>The model this window is scoped to, if any.</summary>
    public string ScopeModel { get; }

    /// <summary>Whether to show the scope line at all.</summary>
    public bool HasScope { get; }

    /// <summary>Yes/no, translated, for the "currently consuming" line.</summary>
    public string ActiveText { get; }

    /// <summary>Whether the source flagged this window as anything but normal.</summary>
    public bool IsWarning { get; }

    /// <summary>Whether the source considers this the window being consumed.</summary>
    public bool IsActive { get; }

    /// <summary>Dollar figures, when the plan reports any.</summary>
    public string DollarsText { get; }

    /// <summary>Whether to show the dollars line at all.</summary>
    public bool HasDollars { get; }

    /// <summary>Why the window is locked, when it is.</summary>
    public string LockedReason { get; }

    /// <summary>Whether to show the locked line at all.</summary>
    public bool IsLocked { get; }

    private static string FormatDollars(ILocalizer localizer, UsageWindow window)
    {
        if (window.UsedDollars is null && window.LimitDollars is null && window.RemainingDollars is null)
        {
            return string.Empty;
        }

        return localizer.Format(
            "Report_Dollars",
            Format(localizer, window.UsedDollars),
            Format(localizer, window.LimitDollars),
            Format(localizer, window.RemainingDollars));

        static string Format(ILocalizer localizer, double? value) => value is { } amount
            ? amount.ToString("N2", localizer.Culture)
            : localizer["Common_Unknown"];
    }
}
