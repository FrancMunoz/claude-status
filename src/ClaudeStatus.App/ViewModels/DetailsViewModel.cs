using Avalonia.Threading;
using ClaudeStatus.Config;
using ClaudeStatus.Localization;
using ClaudeStatus.Update;
using ClaudeStatus.Usage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeStatus.App.ViewModels;

/// <summary>
/// The details window: three bars, freshness, and a Refresh button.
/// </summary>
/// <remarks>
/// Reads from <see cref="IUsageMonitor"/> and never fetches directly, so the
/// window cannot become a second source of requests to a rate-limited endpoint.
/// </remarks>
public partial class DetailsViewModel : ObservableObject, IDisposable
{
    private readonly IUsageMonitor _monitor;
    private readonly TimeProvider _clock;
    private readonly Func<AppSettings> _settings;
    private readonly ILocalizer _l;
    private UsageSnapshot? _lastApplied;
    private IDisposable? _subscription;
    private bool _disposed;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// True when there is a reading and it is old.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="UsageSnapshot.IsStale"/> passed through. That
    /// flag is true before the first poll returns as well, when there is nothing
    /// to be stale - and a banner announcing an old reading above three dashes
    /// says the opposite of what is true. Having no reading yet is
    /// <see cref="StatusText"/>'s job, not this one's.
    /// </remarks>
    [ObservableProperty]
    private bool _hasStaleReading;

    [ObservableProperty]
    private string _staleText = string.Empty;

    /// <summary>The velocity warning, or empty. Set by the controller, which owns the history.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVelocityAlert))]
    private string _velocityText = string.Empty;

    [ObservableProperty]
    private string _sourceName = string.Empty;

    [ObservableProperty]
    private bool _canRefresh = true;

    /// <summary>False until the first reading arrives, and again if it is lost.</summary>
    [ObservableProperty]
    private bool _hasReading;

    /// <summary>Short name for the state the popup is in when there is no reading.</summary>
    [ObservableProperty]
    private string _emptyTitle = string.Empty;

    /// <summary>What the user can do about it, or what is happening on its own.</summary>
    [ObservableProperty]
    private string _emptyBody = string.Empty;

    /// <summary>A new version is downloaded and waiting. Drives the update notice.</summary>
    [ObservableProperty]
    private bool _hasUpdate;

    /// <summary>What the update notice says, already composed. Empty when there is none.</summary>
    [ObservableProperty]
    private string _updateText = string.Empty;

    /// <summary>
    /// True when the empty state is one the user can act on from Config.
    /// </summary>
    /// <remarks>
    /// A missing or rejected credential is something to go and fix; a network
    /// failure or a rate limit is not, and offering a button that opens a window
    /// with nothing relevant in it would be worse than offering none.
    /// </remarks>
    [ObservableProperty]
    private bool _canOpenConfig;

    /// <summary>
    /// Raised when the user asks for the full report.
    /// </summary>
    /// <remarks>
    /// An event rather than a direct call: this view model must not know how
    /// windows are created or owned. The controller owns every window's lifetime,
    /// and it is the only thing that can reuse an already-open report rather than
    /// opening a second one.
    /// </remarks>
    public event EventHandler? ReportRequested;

    /// <summary>Raised when the user asks to open Config from the empty state.</summary>
    /// <remarks>Routed through the controller for the same reason as <see cref="ReportRequested"/>.</remarks>
    public event EventHandler? ConfigRequested;

    /// <summary>Raised when the user asks to restart into a staged update.</summary>
    /// <remarks>
    /// Also routed through the controller: applying an update replaces the
    /// process, and a view model has no business knowing that.
    /// </remarks>
    public event EventHandler? UpdateRequested;

    public DetailsViewModel(
        IUsageMonitor monitor,
        Func<AppSettings> settings,
        ILocalizer localizer,
        TimeProvider? clock = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _clock = clock ?? TimeProvider.System;

        Session = new UsageBarViewModel(_l, "Details_Metric_Session");
        Week = new UsageBarViewModel(_l, "Details_Metric_Week");
        WeekFable = new UsageBarViewModel(_l, "Details_Metric_WeekFable");

        // A language change has to redraw the composed sentences too, not only the
        // static labels, so the last snapshot is replayed through Apply.
        _l.PropertyChanged += (_, _) => Apply(_lastApplied);

        _subscription = _monitor.Snapshots.Subscribe(new SnapshotObserver(this));
        Apply(_monitor.Latest);
    }

    /// <summary>The localizer, so the view can bind static labels to <c>L[Key]</c>.</summary>
    public ILocalizer L => _l;

    /// <summary>The 5-hour-ish session window.</summary>
    public UsageBarViewModel Session { get; }

    /// <summary>The 7-day all-models window.</summary>
    public UsageBarViewModel Week { get; }

    /// <summary>The 7-day Fable-scoped window.</summary>
    public UsageBarViewModel WeekFable { get; }

    /// <summary>Asks the monitor for a fresh reading, subject to its cooldown.</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        CanRefresh = false;
        try
        {
            bool ran = await _monitor.RefreshNowAsync(ct).ConfigureAwait(true);
            if (!ran)
            {
                // Say so rather than appearing to do nothing. The cooldown exists
                // to stop us getting rate limited, and that is worth explaining.
                StatusText = _l["Details_Status_Cooldown"];
            }
        }
        finally
        {
            CanRefresh = true;
        }
    }

    /// <summary>Asks the controller to open the full report window.</summary>
    [RelayCommand]
    private void ShowReport() => ReportRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Asks the controller to open the Config window.</summary>
    [RelayCommand]
    private void OpenConfig() => ConfigRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Asks the controller to restart into the staged update.</summary>
    [RelayCommand]
    private void ApplyUpdateNow() => UpdateRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Shows or clears the "a new version is ready" notice.
    /// </summary>
    /// <remarks>
    /// Only <see cref="UpdateState.ReadyToApply"/> produces a notice. Checking and
    /// downloading are the app's business, not the user's - and a failed check is
    /// least of all: the update is retried on its own, and reporting it would put
    /// a warning about our own release infrastructure in front of somebody who
    /// opened the window to look at a percentage.
    /// </remarks>
    public void ApplyUpdate(UpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        HasUpdate = status.State == UpdateState.ReadyToApply;
        UpdateText = HasUpdate
            ? _l.Format("Update_Ready", status.Version ?? string.Empty)
            : string.Empty;
    }

    /// <summary>Rebuilds every bar from a snapshot.</summary>
    /// <summary>Whether usage is climbing fast enough to warn about.</summary>
    public bool HasVelocityAlert => VelocityText.Length > 0;

    public void Apply(UsageSnapshot? snapshot)
    {
        _lastApplied = snapshot;
        DateTimeOffset now = _clock.GetUtcNow();
        double threshold = _settings().ThresholdPercent;

        (ThresholdState session, ThresholdState week, ThresholdState fable) =
            ThresholdEvaluator.EvaluateAll(snapshot, threshold);

        Session.Update(snapshot?.Session, session, now);
        Week.Update(snapshot?.Week, week, now);
        WeekFable.Update(
            snapshot?.WeekFable, fable, now, snapshot?.WeekFableSharesWeeklyReset ?? false);

        HasReading = snapshot is not null;
        HasStaleReading = snapshot is { IsStale: true };
        StatusText = BuildStatusText(snapshot, now);
        StaleText = HasStaleReading
            ? _l.Format("Details_Stale", DescribeAge(_l, snapshot!.Age(now)))
            : string.Empty;

        ApplyEmptyState(snapshot);
    }

    /// <summary>
    /// Fills in what the popup shows when it has no numbers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before the first reading arrives the popup used to show three empty bars
    /// and three dashes, which says nothing about why. Someone opening the app
    /// for the first time is exactly the person who needs telling what to do, and
    /// a row of dashes reads as "broken" rather than "not set up yet".
    /// </para>
    /// <para>
    /// Each state gets its own pair of strings rather than one apologetic
    /// sentence, because the five differ in the only way that matters: whether
    /// there is anything for the user to do. Two of them are worth acting on and
    /// three resolve themselves.
    /// </para>
    /// </remarks>
    private void ApplyEmptyState(UsageSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            EmptyTitle = string.Empty;
            EmptyBody = string.Empty;
            CanOpenConfig = false;
            return;
        }

        UsageFetchFailure? failure = _monitor.Status.Failure;

        // Both keys written out in full rather than composed from the state name:
        // ResourceKeyUsageTests scans the source for key-shaped literals to prove
        // nothing in the .resx has been orphaned, and it cannot follow a
        // concatenation.
        (string title, string body) = failure switch
        {
            UsageFetchFailure.NoCredential =>
                ("Details_Empty_NoCredential_Title", "Details_Empty_NoCredential_Body"),
            UsageFetchFailure.Unauthorized =>
                ("Details_Empty_Unauthorized_Title", "Details_Empty_Unauthorized_Body"),
            UsageFetchFailure.RateLimited =>
                ("Details_Empty_RateLimited_Title", "Details_Empty_RateLimited_Body"),
            null =>
                ("Details_Empty_Waiting_Title", "Details_Empty_Waiting_Body"),
            _ =>
                ("Details_Empty_Unreachable_Title", "Details_Empty_Unreachable_Body"),
        };

        EmptyTitle = _l[title];
        EmptyBody = _l[body];

        CanOpenConfig = failure
            is UsageFetchFailure.NoCredential
            or UsageFetchFailure.Unauthorized;
    }

    private string BuildStatusText(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null)
        {
            return _l[_monitor.Status.Failure switch
            {
                UsageFetchFailure.NoCredential => "Details_Status_NoCredential",
                UsageFetchFailure.Unauthorized => "Details_Status_Unauthorized",
                UsageFetchFailure.RateLimited => "Details_Status_RateLimited",
                null => "Details_Status_Waiting",
                _ => "Details_Status_Unreachable",
            }];
        }

        return _l.Format("Details_Status_LastUpdated", DescribeAge(_l, snapshot.Age(now)));
    }

    /// <summary>
    /// Describes an age in the coarsest sensible unit.
    /// </summary>
    /// <remarks>
    /// Produces a fragment ("5 min", "2h 10m") that a surrounding sentence embeds.
    /// That is a compromise: it keeps four short keys instead of eight long ones,
    /// at the cost of languages where the fragment would need to inflect. None of
    /// the five shipped languages does, and a translator who needs to can always
    /// rewrite the surrounding sentence to put the fragment somewhere that works.
    /// </remarks>
    internal static string DescribeAge(ILocalizer localizer, TimeSpan age)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        if (age.TotalMinutes < 1)
        {
            return localizer["Age_Moments"];
        }

        if (age.TotalHours < 1)
        {
            return localizer.Format("Age_Minutes", (int)age.TotalMinutes);
        }

        return age.TotalDays < 1
            ? localizer.Format("Age_HoursMinutes", (int)age.TotalHours, age.Minutes)
            : localizer.Format("Age_Days", (int)age.TotalDays);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription?.Dispose();
        _subscription = null;
    }

    /// <summary>
    /// Bridges the monitor's polling thread onto the UI thread.
    /// </summary>
    /// <remarks>
    /// <see cref="UsageMonitor"/> publishes from its poll loop, and every property
    /// this view model sets is bound to a control. Marshalling here rather than in
    /// the monitor keeps Core free of any UI framework.
    /// </remarks>
    private sealed class SnapshotObserver(DetailsViewModel owner) : IObserver<UsageSnapshot>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(UsageSnapshot value)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                owner.Apply(value);
            }
            else
            {
                Dispatcher.UIThread.Post(() => owner.Apply(value));
            }
        }
    }
}
