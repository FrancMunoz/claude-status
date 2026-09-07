using ClaudeStatus.Localization;
using ClaudeStatus.Platform;
using ClaudeStatus.Usage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeStatus.App.ViewModels;

/// <summary>
/// The taskbar widget: all three metrics at once, each with its own bar.
/// </summary>
/// <remarks>
/// <para>
/// The tray icon has room for one number. The widget has room for three, which
/// is the whole reason it exists, so it ignores <see cref="IndicatorMode"/> and
/// evaluates every metric against the threshold itself.
/// </para>
/// <para>
/// Reuses <see cref="UsageBarViewModel"/> for each metric rather than inventing a
/// smaller one: the percentage formatting and the "unknown is a dash, not a zero"
/// rule are already right there, and the widget must agree with the popup.
/// </para>
/// </remarks>
public partial class TaskbarWidgetViewModel : ObservableObject
{
    private readonly ILocalizer _l;

    [ObservableProperty]
    private bool _hasReading;

    [ObservableProperty]
    private bool _isStale;

    /// <summary>
    /// Whether the Fable column is shown. Off by default - see
    /// <see cref="Config.AppSettings.ShowFableInWidget"/>.
    /// </summary>
    [ObservableProperty]
    private bool _showFable;

    /// <summary>
    /// Whether the widget blends into the taskbar rather than drawing the themed
    /// card - see <see cref="Config.AppSettings.WidgetFollowsSystem"/>.
    /// </summary>
    [ObservableProperty]
    private bool _followsSystem = true;

    /// <summary>The symbol shown instead of the metrics when there is nothing to show.</summary>
    [ObservableProperty]
    private string _alertGlyph = string.Empty;

    /// <summary>The short sentence beside <see cref="AlertGlyph"/>.</summary>
    [ObservableProperty]
    private string _alertText = string.Empty;

    /// <summary>The hover card's status line: how old the reading is, or why there is none.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>A warning shown at the foot of the hover card; empty when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _noticeText = string.Empty;

    /// <summary>Whether <see cref="NoticeText"/> has anything to show.</summary>
    public bool HasNotice => NoticeText.Length > 0;

    private UsageSnapshot? _snapshot;
    private IndicatorAlert _alert;
    private DateTimeOffset _now;

    public TaskbarWidgetViewModel(ILocalizer localizer)
    {
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));

        Session = new UsageBarViewModel(_l, "Widget_Session");
        Week = new UsageBarViewModel(_l, "Widget_Week");
        WeekFable = new UsageBarViewModel(_l, "Widget_Fable");

        TooltipSession = new UsageBarViewModel(_l, "Details_Metric_Session");
        TooltipWeek = new UsageBarViewModel(_l, "Details_Metric_Week");
        TooltipWeekFable = new UsageBarViewModel(_l, "Details_Metric_WeekFable");

        // The alert sentence is composed here, so a language change has to redo it.
        _l.PropertyChanged += (_, _) => Refresh();
    }

    /// <summary>The shared localizer, for <c>L[Key]</c> bindings.</summary>
    public ILocalizer L => _l;

    public UsageBarViewModel Session { get; }

    public UsageBarViewModel Week { get; }

    public UsageBarViewModel WeekFable { get; }

    /// <summary>
    /// The same three metrics under their full names, for the hover card.
    /// </summary>
    /// <remarks>
    /// Separate instances rather than the widget's own because a
    /// <see cref="UsageBarViewModel"/> carries one label key, and the widget wants
    /// "5h" where the hover card wants "Session". They are updated together.
    /// </remarks>
    public UsageBarViewModel TooltipSession { get; }

    public UsageBarViewModel TooltipWeek { get; }

    public UsageBarViewModel TooltipWeekFable { get; }

    /// <summary>Where a metric turns red. Set by the controller from settings.</summary>
    public double ThresholdPercent { get; private set; } = 80d;

    /// <summary>Applies the settings and re-evaluates the current reading.</summary>
    public void Configure(double thresholdPercent, bool showFable, bool followSystem = false)
    {
        ThresholdPercent = thresholdPercent;
        ShowFable = showFable;
        FollowsSystem = followSystem;
        Refresh();
    }

    /// <summary>Shows a reading, or the reason there is none.</summary>
    public void Update(UsageSnapshot? snapshot, IndicatorAlert alert, DateTimeOffset now)
    {
        _snapshot = snapshot;
        _alert = alert;
        _now = now;
        Refresh();
    }

    private void Refresh()
    {
        // Same precedence as the tray icon: a missing credential is actionable
        // and wins over everything; an unreachable endpoint only matters when
        // there is no reading at all to fall back on.
        if (_alert == IndicatorAlert.NeedsCredential)
        {
            ShowAlert("!", _l["Widget_NeedsCredential"]);
            return;
        }

        if (_snapshot is null)
        {
            ShowAlert(
                _alert == IndicatorAlert.Unreachable ? "⊘" : "—",
                _l[_alert == IndicatorAlert.Unreachable ? "Widget_Offline" : "Widget_NoData"]);
            return;
        }

        HasReading = true;
        IsStale = _snapshot.IsStale;
        AlertGlyph = string.Empty;
        AlertText = string.Empty;

        // The same wording the popup uses, so hovering and clicking agree.
        string age = DetailsViewModel.DescribeAge(_l, _snapshot.Age(_now));
        StatusText = _l.Format(IsStale ? "Details_Stale" : "Details_Status_LastUpdated", age);

        ThresholdState session = Evaluate(_snapshot.Session);
        ThresholdState week = Evaluate(_snapshot.Week);
        ThresholdState fable = Evaluate(_snapshot.WeekFable);

        Session.Update(_snapshot.Session, session, _now);
        Week.Update(_snapshot.Week, week, _now);
        WeekFable.Update(_snapshot.WeekFable, fable, _now, _snapshot.WeekFableSharesWeeklyReset);

        TooltipSession.Update(_snapshot.Session, session, _now);
        TooltipWeek.Update(_snapshot.Week, week, _now);
        TooltipWeekFable.Update(_snapshot.WeekFable, fable, _now, _snapshot.WeekFableSharesWeeklyReset);
    }

    private ThresholdState Evaluate(UsageWindow? window)
        => ThresholdEvaluator.Evaluate(window, ThresholdPercent);

    private void ShowAlert(string glyph, string text)
    {
        HasReading = false;
        IsStale = false;
        AlertGlyph = glyph;
        AlertText = text;
        StatusText = text;
    }
}
