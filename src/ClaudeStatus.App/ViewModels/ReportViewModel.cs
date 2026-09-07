using System.Collections.ObjectModel;
using Avalonia.Threading;
using ClaudeStatus.Localization;
using ClaudeStatus.Platform;
using ClaudeStatus.Usage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeStatus.App.ViewModels;

/// <summary>
/// The full report: everything the endpoint reported, not just what fits in the
/// popup.
/// </summary>
/// <remarks>
/// <para>
/// The details popup answers "how much have I used" at a glance and is meant to
/// be dismissed. This window is the opposite: it is for looking things up, so it
/// shows every window the source returned, the source's own severity labels, the
/// exact reset timestamps, and the spend and credit blocks - including when they
/// are switched off, because "off" is itself the answer to a question.
/// </para>
/// <para>
/// Like the popup, it reads from <see cref="IUsageMonitor"/> and never fetches on
/// its own, so opening it cannot add load to a rate-limited endpoint.
/// </para>
/// </remarks>
public partial class ReportViewModel : ObservableObject, IDisposable
{
    private readonly IUsageMonitor _monitor;
    private readonly IUsageProvider _provider;
    private readonly IPlatformInfo _platform;
    private readonly TimeProvider _clock;
    private readonly ILocalizer _l;
    private UsageSnapshot? _lastApplied;
    private IDisposable? _subscription;
    private bool _disposed;

    [ObservableProperty]
    private string _fetchedAtText = string.Empty;

    [ObservableProperty]
    private string _freshnessText = string.Empty;

    /// <summary>
    /// True when there is a reading and it is old.
    /// </summary>
    /// <remarks>
    /// False before the first poll returns, even though
    /// <see cref="UsageSnapshot.IsStale"/> would be true there. The banner this
    /// drives says "these are the last known values", and showing that above an
    /// empty report claims values that do not exist. Having no reading yet is
    /// <see cref="HasReading"/>'s job.
    /// </remarks>
    [ObservableProperty]
    private bool _hasStaleReading;

    [ObservableProperty]
    private string _sourceText = string.Empty;

    [ObservableProperty]
    private bool _canRefresh = true;

    [ObservableProperty]
    private bool _hasReading;

    [ObservableProperty]
    private string _spendText = string.Empty;

    [ObservableProperty]
    private bool _spendEnabled;

    [ObservableProperty]
    private string _creditsText = string.Empty;

    [ObservableProperty]
    private bool _creditsEnabled;

    [ObservableProperty]
    private bool _creditsLimitReached;

    public ReportViewModel(
        IUsageMonitor monitor,
        IUsageProvider provider,
        IPlatformInfo platform,
        ILocalizer localizer,
        TimeProvider? clock = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _clock = clock ?? TimeProvider.System;

        _l.PropertyChanged += (_, _) => Apply(_lastApplied);

        _subscription = _monitor.Snapshots.Subscribe(new SnapshotObserver(this));
        Apply(_monitor.Latest);
    }

    /// <summary>The localizer, so the view can bind static labels to <c>L[Key]</c>.</summary>
    public ILocalizer L => _l;

    /// <summary>One row per usage window the source reported.</summary>
    public ObservableCollection<ReportRowViewModel> Rows { get; } = [];

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
                FreshnessText = _l["Details_Status_Cooldown"];
            }
        }
        finally
        {
            CanRefresh = true;
        }
    }

    /// <summary>Rebuilds every row and block from a snapshot.</summary>
    public void Apply(UsageSnapshot? snapshot)
    {
        _lastApplied = snapshot;
        DateTimeOffset now = _clock.GetUtcNow();

        Rows.Clear();
        HasReading = snapshot is not null;

        SourceText = _l.Format(
            "Info_DataSource", _l.Format(_provider.NameKey, _provider.NameArgument));

        if (snapshot is null)
        {
            HasStaleReading = false;
            FetchedAtText = string.Empty;
            FreshnessText = _l["Details_Status_Waiting"];
            SpendText = string.Empty;
            CreditsText = string.Empty;
            SpendEnabled = false;
            CreditsEnabled = false;
            CreditsLimitReached = false;
            return;
        }

        foreach ((string label, bool isResourceKey, UsageWindow window) in snapshot.AllWindows())
        {
            Rows.Add(new ReportRowViewModel(
                _l,
                isResourceKey ? _l[label] : label,
                window,
                now,
                ReferenceEquals(window, snapshot.WeekFable) && snapshot.WeekFableSharesWeeklyReset));
        }

        HasStaleReading = snapshot.IsStale;
        FetchedAtText = _l.Format(
            "Report_FetchedAt", snapshot.FetchedAt.ToLocalTime().ToString("f", _l.Culture));
        FreshnessText = snapshot.IsStale
            ? _l.Format("Details_Stale", DetailsViewModel.DescribeAge(_l, snapshot.Age(now)))
            : _l.Format("Details_Status_LastUpdated", DetailsViewModel.DescribeAge(_l, snapshot.Age(now)));

        ApplySpend(snapshot.Spend);
        ApplyCredits(snapshot.ExtraUsage);
    }

    /// <summary>
    /// Describes the pay-as-you-go block.
    /// </summary>
    /// <remarks>
    /// "Off" is stated plainly rather than left as an empty section. A user
    /// wondering whether they are being charged past their plan wants that
    /// answered, and a blank panel does not answer it.
    /// </remarks>
    private void ApplySpend(SpendInfo? spend)
    {
        if (spend is null)
        {
            SpendEnabled = false;
            SpendText = _l["Report_Spend_NotReported"];
            return;
        }

        SpendEnabled = spend.Enabled;

        if (!spend.Enabled)
        {
            SpendText = _l["Report_Spend_Off"];
            return;
        }

        SpendText = _l.Format(
            "Report_Spend_On",
            spend.Used?.Format(_l.Culture) ?? _l["Common_Unknown"],
            spend.Limit?.Format(_l.Culture) ?? _l["Common_NotAvailable"],
            spend.Percent is { } percent
                ? _l.Format("Bar_Percent", percent)
                : _l["Common_Unknown"]);
    }

    /// <summary>Describes the prepaid-credits block.</summary>
    private void ApplyCredits(ExtraUsageInfo? credits)
    {
        if (credits is null)
        {
            CreditsEnabled = false;
            CreditsLimitReached = false;
            CreditsText = _l["Report_Credits_NotReported"];
            return;
        }

        CreditsEnabled = credits.IsEnabled;
        CreditsLimitReached = credits.SpendLimitReached;

        if (!credits.IsEnabled)
        {
            // Never switched on and deliberately switched off are different states
            // and lead to different next actions, so they get different sentences.
            CreditsText = _l[credits.CreditsEverEnabled
                ? "Report_Credits_TurnedOff"
                : "Report_Credits_NeverUsed"];
            return;
        }

        CreditsText = _l.Format(
            "Report_Credits_On",
            credits.UsedCredits?.ToString("N2", _l.Culture) ?? _l["Common_Unknown"],
            credits.MonthlyLimit?.ToString("N2", _l.Culture) ?? _l["Common_NotAvailable"],
            credits.Utilization is { } utilization
                ? _l.Format("Bar_Percent", utilization)
                : _l["Common_Unknown"]);
    }

    /// <summary>Where the log lives, so a bug report can point at it.</summary>
    public string LogFileText => _l.Format(
        "Info_LogFile",
        System.IO.Path.Combine(
            _platform.ConfigDirectory, Logging.RollingFileLoggerProvider.FileName));

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

    /// <summary>Bridges the monitor's polling thread onto the UI thread.</summary>
    private sealed class SnapshotObserver(ReportViewModel owner) : IObserver<UsageSnapshot>
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
