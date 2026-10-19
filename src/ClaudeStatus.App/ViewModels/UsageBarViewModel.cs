using ClaudeStatus.Localization;
using ClaudeStatus.Usage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeStatus.App.ViewModels;

/// <summary>
/// One usage bar in the details window: label, percentage and reset countdown.
/// </summary>
/// <remarks>
/// A missing window shows a dash and an empty bar, never a zero. Zero and
/// "we do not know" mean very different things to someone deciding whether to
/// start a long task.
/// </remarks>
public partial class UsageBarViewModel : ObservableObject
{
    private readonly ILocalizer _l;
    private readonly string _labelKey;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _percentText = "—";

    /// <summary>
    /// The same reading with the per-cent sign left off, for the widget.
    /// </summary>
    /// <remarks>
    /// The widget sets the sign in its own smaller type and tight against the
    /// number, which one string cannot express - so the split is here, where the
    /// two halves are still separately translatable, rather than in the view
    /// chopping up <see cref="PercentText"/>. Everywhere with room to breathe
    /// keeps <see cref="PercentText"/> and the spacing its language asks for.
    /// </remarks>
    [ObservableProperty]
    private string _percentNumberText = "—";

    [ObservableProperty]
    private string _resetText = string.Empty;

    [ObservableProperty]
    private bool _isKnown;

    [ObservableProperty]
    private bool _isExceeded;

    /// <summary>
    /// How far this window has travelled towards its own reset, 0-100.
    /// </summary>
    /// <remarks>
    /// Time, not usage: at 40 the window is 40 % of the way through its span,
    /// whatever <see cref="Percent"/> says. Zero, and meaningless, unless
    /// <see cref="HasTimeProgress"/> is set.
    /// </remarks>
    [ObservableProperty]
    private double _timePercent;

    /// <summary>
    /// Whether <see cref="TimePercent"/> means anything: there is a reading, the
    /// source gave a reset time, and this bar was told how long its window is.
    /// </summary>
    [ObservableProperty]
    private bool _hasTimeProgress;

    /// <summary>
    /// What is left of the window as a bracketed clock, <c>(2:37)</c>.
    /// </summary>
    /// <remarks>
    /// The terse twin of <see cref="ResetText"/>, for the widget, where "resets in
    /// 2h 37m" would not fit and would be re-read every glance anyway. Only short
    /// windows get one - see <see cref="ClockCeiling"/> - so it is empty unless
    /// <see cref="HasClock"/> says otherwise.
    /// </remarks>
    [ObservableProperty]
    private string _clockText = string.Empty;

    /// <summary>Whether <see cref="ClockText"/> has a countdown to show.</summary>
    [ObservableProperty]
    private bool _hasClock;

    /// <summary>
    /// The longest window that gets a <see cref="ClockText"/>.
    /// </summary>
    /// <remarks>
    /// The ceiling is on the window's own length, not on what is left of it: a
    /// weekly window in its final hours would otherwise sprout a clock for one day
    /// in seven, which is a layout that changes shape once a week. What the clock
    /// then says is <see cref="IndicatorText.FormatCountdown"/>'s business, and it
    /// applies the same ceiling to the remaining time.
    /// </remarks>
    private static readonly TimeSpan ClockCeiling = IndicatorText.CountdownCeiling;

    /// <summary>
    /// The fill's split by surface, biggest first, for <see cref="Controls.UsageBar.Segments"/>.
    /// Null when this bar has no split.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<double>? _segments;

    /// <summary>Whether <see cref="SplitKeys"/> has anything to show.</summary>
    [ObservableProperty]
    private bool _hasSplit;

    /// <summary>The legend for <see cref="Segments"/>, in the same order and shades.</summary>
    public System.Collections.ObjectModel.ObservableCollection<UsageShareKeyViewModel> SplitKeys { get; } = [];

    private readonly TimeSpan? _span;
    private UsageWindow? _window;
    private ThresholdState _state;
    private DateTimeOffset _asOf;
    private bool _sharesWeeklyReset;
    private UsageBreakdown? _split;

    /// <param name="localizer">Shared localizer; the bar re-renders when it changes language.</param>
    /// <param name="labelKey">Resource key for the metric name, e.g. <c>"Details_Metric_Session"</c>.</param>
    /// <param name="span">
    /// The nominal length of this window - five hours, seven days - which is what
    /// turns the countdown into <see cref="TimePercent"/>. Null everywhere the
    /// elapsed share is not drawn, and null is the honest default: the source says
    /// when a window resets and never how long it is, so the span is our assumption
    /// about the plan rather than a fact from the endpoint. It stays out of
    /// <see cref="ResetText"/>, which is derived from the reset time alone.
    /// </param>
    public UsageBarViewModel(ILocalizer localizer, string labelKey, TimeSpan? span = null)
    {
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _labelKey = labelKey ?? throw new ArgumentNullException(nameof(labelKey));
        _span = span > TimeSpan.Zero ? span : null;

        // Re-derive the text rather than only re-reading the label: the countdown
        // and the percentage are composed here too, so a language change has to
        // rebuild all three or the bar ends up half translated.
        _l.PropertyChanged += (_, _) => Refresh();
    }

    /// <summary>The metric name, in the current language.</summary>
    public string Label => _l[_labelKey];

    /// <summary>The per-cent sign that goes after <see cref="PercentNumberText"/>.</summary>
    public string PercentSign => _l["Bar_PercentSign"];

    /// <summary>Refreshes this bar from a reading.</summary>
    /// <param name="sharesWeeklyReset">
    /// True when this window rolls over with the weekly one, in which case the
    /// countdown is replaced by a note saying so - see
    /// <see cref="UsageSnapshot.WeekFableSharesWeeklyReset"/>.
    /// </param>
    /// <param name="split">
    /// Where this window's usage went, by surface. Only the weekly all-models bar
    /// is given one: the breakdown is of that window, not of the others.
    /// </param>
    public void Update(
        UsageWindow? window,
        ThresholdState state,
        DateTimeOffset now,
        bool sharesWeeklyReset = false,
        UsageBreakdown? split = null)
    {
        _window = window;
        _state = state;
        _asOf = now;
        _sharesWeeklyReset = sharesWeeklyReset;
        _split = split;
        Refresh();
    }

    /// <summary>
    /// The surfaces worth drawing, biggest first, at most one per shade.
    /// </summary>
    /// <remarks>
    /// Biggest first so the strongest shade is always the largest share, whatever
    /// order the endpoint sent. Below half a percent a segment is narrower than
    /// the gap around it, so it is left out. The iCUE widget ranks the same way.
    /// </remarks>
    internal static IReadOnlyList<UsageBreakdownRow> RankSurfaces(UsageBreakdown? split)
        => split is null
            ? []
            : [.. split.Rows
                .Where(row => row.Percent >= 0.5)
                .OrderByDescending(row => row.Percent)
                .Take(Controls.UsageBar.SegmentShades.Count)];

    private void RefreshSplit()
    {
        SplitKeys.Clear();
        IReadOnlyList<UsageBreakdownRow> ranked = _window is null ? [] : RankSurfaces(_split);

        // One surface is no split at all: the plain fill says the same thing.
        HasSplit = ranked.Count > 1;
        Segments = HasSplit ? [.. ranked.Select(row => row.Percent)] : null;
        if (!HasSplit)
        {
            return;
        }

        for (int index = 0; index < ranked.Count; index++)
        {
            SplitKeys.Add(new UsageShareKeyViewModel(
                ReportShareViewModel.Name(_l, ranked[index]),
                _l.Format("Bar_Percent", ranked[index].Percent),
                Controls.UsageBar.SegmentShades[index]));
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(PercentSign));

        RefreshSplit();
        IsKnown = _window is not null;
        IsExceeded = _state == ThresholdState.Exceeded;

        if (_window is null)
        {
            Percent = 0;
            PercentText = _l["Common_Unknown"];
            PercentNumberText = _l["Common_Unknown"];
            ResetText = _l["Bar_NoData"];
            HasTimeProgress = false;
            TimePercent = 0;
            HasClock = false;
            ClockText = string.Empty;
            return;
        }

        Percent = _window.Percent;
        PercentText = _l.Format("Bar_Percent", _window.Percent);
        PercentNumberText = _l.Format("Bar_PercentNumber", _window.Percent);

        // A window that rolls over with the weekly one says so instead of either
        // repeating the identical countdown from the line above or, worse,
        // reporting "unknown" for a reset time the endpoint simply did not repeat.
        ResetText = _sharesWeeklyReset
            ? _l["Reset_WithWeekly"]
            : FormatReset(_l, _window.TimeUntilReset(_asOf));

        // Clamped rather than trusted, exactly as the percentage is. More time
        // left than the span we assumed means we assumed the wrong plan, and an
        // empty bar is a better answer to that than a negative one.
        TimeSpan? remaining = _window.TimeUntilReset(_asOf);
        HasTimeProgress = _span is not null && remaining is not null;
        TimePercent = _span is { } span && remaining is { } left
            ? Math.Clamp((span - left).TotalSeconds / span.TotalSeconds * 100d, 0d, 100d)
            : 0d;

        // The clock hangs off the same span as the bar under it, so a window with
        // no length assumed for it shows neither, and the two can never disagree.
        // The shape comes from IndicatorText, shared with the macOS menu bar,
        // which writes the same "5h (2:11) 56%" one line up.
        ClockText = _span < ClockCeiling && !_sharesWeeklyReset
            ? IndicatorText.FormatCountdown(remaining)
            : string.Empty;
        HasClock = ClockText.Length > 0;
    }

    /// <summary>
    /// Renders a countdown the way a person would say it.
    /// </summary>
    /// <remarks>
    /// Deliberately coarse. Nobody needs seconds, and showing them would make the
    /// window look like it is doing more work than it is. Each shape is a separate
    /// resource key rather than one template with optional parts, because word
    /// order and which units get named differ between languages.
    /// </remarks>
    internal static string FormatReset(ILocalizer localizer, TimeSpan? until)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        if (until is null)
        {
            return localizer["Reset_Unknown"];
        }

        TimeSpan value = until.Value;
        if (value <= TimeSpan.Zero)
        {
            return localizer["Reset_Now"];
        }

        if (value.TotalDays >= 1)
        {
            int days = (int)value.TotalDays;
            int hours = value.Hours;
            return hours > 0
                ? localizer.Format("Reset_DaysHours", days, hours)
                : localizer.Format("Reset_Days", days);
        }

        if (value.TotalHours >= 1)
        {
            return localizer.Format("Reset_HoursMinutes", (int)value.TotalHours, value.Minutes);
        }

        return value.TotalMinutes >= 1
            ? localizer.Format("Reset_Minutes", (int)value.TotalMinutes)
            : localizer["Reset_UnderMinute"];
    }
}
