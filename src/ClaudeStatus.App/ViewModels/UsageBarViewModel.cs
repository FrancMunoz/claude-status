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

    [ObservableProperty]
    private string _resetText = string.Empty;

    [ObservableProperty]
    private bool _isKnown;

    [ObservableProperty]
    private bool _isExceeded;

    private UsageWindow? _window;
    private ThresholdState _state;
    private DateTimeOffset _asOf;
    private bool _sharesWeeklyReset;

    /// <param name="localizer">Shared localizer; the bar re-renders when it changes language.</param>
    /// <param name="labelKey">Resource key for the metric name, e.g. <c>"Details_Metric_Session"</c>.</param>
    public UsageBarViewModel(ILocalizer localizer, string labelKey)
    {
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _labelKey = labelKey ?? throw new ArgumentNullException(nameof(labelKey));

        // Re-derive the text rather than only re-reading the label: the countdown
        // and the percentage are composed here too, so a language change has to
        // rebuild all three or the bar ends up half translated.
        _l.PropertyChanged += (_, _) => Refresh();
    }

    /// <summary>The metric name, in the current language.</summary>
    public string Label => _l[_labelKey];

    /// <summary>Refreshes this bar from a reading.</summary>
    /// <param name="sharesWeeklyReset">
    /// True when this window rolls over with the weekly one, in which case the
    /// countdown is replaced by a note saying so - see
    /// <see cref="UsageSnapshot.WeekFableSharesWeeklyReset"/>.
    /// </param>
    public void Update(
        UsageWindow? window,
        ThresholdState state,
        DateTimeOffset now,
        bool sharesWeeklyReset = false)
    {
        _window = window;
        _state = state;
        _asOf = now;
        _sharesWeeklyReset = sharesWeeklyReset;
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Label));

        IsKnown = _window is not null;
        IsExceeded = _state == ThresholdState.Exceeded;

        if (_window is null)
        {
            Percent = 0;
            PercentText = _l["Common_Unknown"];
            ResetText = _l["Bar_NoData"];
            return;
        }

        Percent = _window.Percent;
        PercentText = _l.Format("Bar_Percent", _window.Percent);

        // A window that rolls over with the weekly one says so instead of either
        // repeating the identical countdown from the line above or, worse,
        // reporting "unknown" for a reset time the endpoint simply did not repeat.
        ResetText = _sharesWeeklyReset
            ? _l["Reset_WithWeekly"]
            : FormatReset(_l, _window.TimeUntilReset(_asOf));
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
