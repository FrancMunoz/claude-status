using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using ClaudeStatus.App.Composition;
using ClaudeStatus.App.Theming;
using ClaudeStatus.App.Tray;
using ClaudeStatus.App.Update;
using ClaudeStatus.App.ViewModels;
using ClaudeStatus.App.Views;
using ClaudeStatus.Config;
using ClaudeStatus.Localization;
using ClaudeStatus.Platform;
using ClaudeStatus.Security;
using ClaudeStatus.Theming;
using ClaudeStatus.Update;
using ClaudeStatus.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeStatus.App;

/// <summary>
/// Owns the running application: the monitor, the tray indicator, and the windows.
/// </summary>
/// <remarks>
/// <para>
/// There is no main window. The lifetime is <see cref="ShutdownMode.OnExplicitShutdown"/>,
/// so closing a window never ends the process - only Quit does.
/// </para>
/// <para>
/// Windows are created once and hidden rather than closed, so reopening is
/// instant and the details view model keeps its subscription.
/// </para>
/// </remarks>
public sealed class TrayApplicationController : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly Func<IndicatorKind, IStatusIndicator> _indicatorFactory;
    private readonly IConfigStore _configStore;

    /// <summary>
    /// The indicator in use. Created from settings in <see cref="StartAsync"/> and
    /// replaced in place when the user switches kind, so never null after start.
    /// </summary>
    private IStatusIndicator _indicator;
    private readonly ISnapshotCache _snapshotCache;

    /// <summary>Recent session readings, for the velocity rule. Fresh samples only.</summary>
    private readonly UsageHistory _sessionHistory = new();

    /// <summary>Recent weekly readings, for the velocity rule.</summary>
    private readonly UsageHistory _weekHistory = new();

    /// <summary>How long a sustained fast pace waits before it flashes the notice again.</summary>
    private static readonly TimeSpan VelocityRenotify = TimeSpan.FromMinutes(15);

    /// <summary>How long the velocity notice stays up when the widget flashes it.</summary>
    private static readonly TimeSpan VelocityNoticeDuration = TimeSpan.FromSeconds(8);

    /// <summary>When the notice was last flashed, so a steady pace does not renotify every poll.</summary>
    private DateTimeOffset _lastVelocityNotice = DateTimeOffset.MinValue;

    private UsageMonitor? _monitor;
    private DetailsViewModel? _detailsViewModel;
    private DetailsWindow? _detailsWindow;
    private ReportViewModel? _reportViewModel;
    private ReportWindow? _reportWindow;
    private ConfigWindow? _configWindow;
    private InfoWindow? _infoWindow;
    private AppSettings _settings = new();

    /// <summary>When the popup last hid itself through losing focus.</summary>
    /// <remarks>
    /// Stamped so a tray click arriving immediately afterwards can be recognised
    /// as the other half of that same gesture rather than a request to reopen.
    /// </remarks>
    private DateTimeOffset _detailsDismissedAt = DateTimeOffset.MinValue;

    /// <summary>The updater, or a no-op one when the setting is off. Never null after start.</summary>
    private IUpdateService _updates = new NullUpdateService();

    /// <summary>Drives the periodic update check. Null while updates are off.</summary>
    private Timer? _updateTimer;

    private bool _disposed;

    /// <param name="indicatorFactory">
    /// Builds an indicator of a kind. Defaults to <see cref="CreateIndicator"/>;
    /// tests pass a factory that hands back a fake.
    /// </param>
    public TrayApplicationController(
        IServiceProvider services,
        IClassicDesktopStyleApplicationLifetime lifetime,
        Func<IndicatorKind, IStatusIndicator>? indicatorFactory = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _indicatorFactory = indicatorFactory ?? (kind => CreateIndicator(services, kind));
        _configStore = services.GetRequiredService<IConfigStore>();
        _snapshotCache = services.GetRequiredService<ISnapshotCache>();

        // The settings are not loaded yet, so this is the icon: it needs no
        // taskbar and is what every other kind falls back to. StartAsync swaps it
        // for the configured kind before anything is shown.
        _indicator = _indicatorFactory(IndicatorKind.TrayIcon);
        Wire(_indicator);
    }

    /// <summary>
    /// Builds the indicator the settings ask for, or the one that can actually run.
    /// </summary>
    /// <remarks>
    /// The widget needs a horizontal taskbar to live in. Without one - macOS, Linux,
    /// a vertical Windows taskbar - asking for it gets the icon, quietly: the
    /// Config window hides the choice in that case, so this is only reached with a
    /// settings file written on another setup.
    /// </remarks>
    public static IStatusIndicator CreateIndicator(IServiceProvider services, IndicatorKind kind)
    {
        ArgumentNullException.ThrowIfNull(services);

        ITaskbarHost host = services.GetRequiredService<ITaskbarHost>();
        if (kind == IndicatorKind.TaskbarWidget && host.IsSupported)
        {
            return new TaskbarWidgetIndicator(
                host,
                services.GetRequiredService<ILocalizer>(),
                services.GetRequiredService<TimeProvider>(),
                services.GetRequiredService<ITrayThemeProvider>(),
                services.GetRequiredService<ILogger<TaskbarWidgetIndicator>>());
        }

        return new TrayIconIndicator(
            services.GetRequiredService<ILocalizer>(),
            services.GetRequiredService<ITrayThemeProvider>());
    }

    private void Wire(IStatusIndicator indicator)
    {
        indicator.LeftClicked += OnIndicatorLeftClicked;
        indicator.MenuAction += OnMenuAction;
    }

    private void OnIndicatorLeftClicked(object? sender, EventArgs e) => OnLeftClicked();

    /// <summary>
    /// Replaces the indicator with one of the given kind, live.
    /// </summary>
    /// <remarks>
    /// Dispose first, then create: the tray icon and the widget could coexist for
    /// a moment, but the old one disappearing before the new one appears is the
    /// order a user reads as "it switched" rather than "it doubled".
    /// </remarks>
    private void SwitchIndicator(IndicatorKind kind)
    {
        _indicator.LeftClicked -= OnIndicatorLeftClicked;
        _indicator.MenuAction -= OnMenuAction;
        _indicator.Dispose();

        _indicator = _indicatorFactory(kind);
        Wire(_indicator);
        _indicator.Configure(IndicatorOptionsFrom(_settings));
        _indicator.Show();
    }

    private static IndicatorOptions IndicatorOptionsFrom(AppSettings settings)
        => new(settings.ThresholdPercent, settings.ShowFableInWidget, settings.WidgetFollowsSystem);

    /// <summary>The settings currently in force.</summary>
    public AppSettings Settings => _settings;

    /// <summary>Loads settings, starts polling, and shows the tray icon.</summary>
    public async Task StartAsync()
    {
        _settings = await _configStore.LoadAsync().ConfigureAwait(true);

        // The constructor could only build the icon. Now that the settings are
        // known, build what they ask for - before Show, so nothing flashes.
        if (_settings.Indicator != IndicatorKind.TrayIcon)
        {
            _indicator.LeftClicked -= OnIndicatorLeftClicked;
            _indicator.MenuAction -= OnMenuAction;
            _indicator.Dispose();
            _indicator = _indicatorFactory(_settings.Indicator);
            Wire(_indicator);
        }

        // Before anything is rendered, so the first frame is already in the right
        // language and colours, and the tray menu is built once rather than built
        // and relabelled.
        ApplyLanguage();
        ApplyTheme();

        _services.GetRequiredService<ILogger<TrayApplicationController>>().LogInformation(
            "ClaudeStatus starting. Source: {Source}. Poll interval: {Interval}.",
            _settings.CredentialSource,
            _settings.Polling.BaseInterval);

        StartMonitor();

        // Seed from disk before the first poll so the tray is populated instantly,
        // and stays populated when the machine is offline. Marked stale, honestly.
        UsageSnapshot? cached = await _snapshotCache.LoadAsync().ConfigureAwait(true);
        if (cached is not null)
        {
            _monitor?.SeedFrom(cached);
        }

        _indicator.Show();
        RenderIndicator(_monitor?.Latest);

        StartUpdates();

        // First run means there is no settings file at all. Open Config so the
        // user is not left staring at a grey icon wondering what to do.
        if (!_configStore.Exists)
        {
            ShowConfig();
        }
    }

    /// <summary>
    /// Puts the localizer into the language the settings ask for.
    /// </summary>
    /// <remarks>
    /// An empty <see cref="AppSettings.LanguageTag"/> means "follow the system",
    /// which is the default. <see cref="CultureInfo.InstalledUICulture"/> rather
    /// than <c>CurrentUICulture</c> is the right source: the latter is whatever we
    /// last set, so reading it here would make the setting sticky in a way the user
    /// never asked for.
    /// </remarks>
    private void ApplyLanguage()
    {
        Localizer localizer = _services.GetRequiredService<Localizer>();
        localizer.SetCulture(LanguageCatalog.Resolve(
            _settings.LanguageTag, localizer.AvailableTags(), CultureInfo.InstalledUICulture));
    }

    /// <summary>
    /// Puts the chosen theme, font and OSD opacity into the application resources.
    /// </summary>
    /// <remarks>
    /// The "system" theme follows the same signal the tray icon uses for its own
    /// contrast, so the app and its icon agree about whether the desktop is dark.
    /// Reading it fresh here means a theme change on the OS is picked up the next
    /// time settings are applied.
    /// </remarks>
    private void ApplyTheme()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        bool systemIsDark =
            _services.GetRequiredService<ITrayThemeProvider>().Current != TrayBackground.Light;

        Theme theme = ThemeCatalog.Resolve(
            _settings.ThemeId,
            _services.GetRequiredService<JsonThemeStore>().All(),
            systemIsDark);

        ThemeApplier.Apply(application, theme, _settings.FontFamily, _settings.OsdTransparency);

        // Runs on every settings change, which is exactly when these can move.
        _indicator.Configure(IndicatorOptionsFrom(_settings));
    }

    /// <summary>
    /// Starts, restarts or stops the update checker to match the settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Turning the setting off disposes the service and the timer rather than
    /// leaving them running and hiding the result. This is the only request the
    /// app makes to anything other than Anthropic, so "off" has to mean no
    /// traffic, not a suppressed notice.
    /// </para>
    /// <para>
    /// The first check is delayed rather than run at startup. A tray app is
    /// launched at login alongside everything else on the machine, and a release
    /// that has been out for weeks does not need to be discovered in the first
    /// second of a cold boot.
    /// </para>
    /// </remarks>
    private void StartUpdates()
    {
        _updateTimer?.Dispose();
        _updateTimer = null;
        _updates.Dispose();

        if (!_settings.AutomaticUpdates)
        {
            _updates = new NullUpdateService();
            return;
        }

        var service = new VelopackUpdateService(
            InfoViewModel.ProjectUrl,
            _services.GetRequiredService<ILogger<VelopackUpdateService>>());

        _updates = service;

        // Logged either way. "Why did it never update?" is otherwise unanswerable
        // from a bug report: an unsupported build and a healthy one that simply
        // found nothing look identical from the outside.
        _services.GetRequiredService<ILogger<TrayApplicationController>>().LogInformation(
            "Update checks: {State}. Installed version: {Version}.",
            service.Status.State,
            service.CurrentVersion ?? "unknown");

        if (service.Status.State == UpdateState.Unsupported)
        {
            // A build running from bin/ or a portable copy. Nothing to schedule.
            return;
        }

        service.StatusChanged += (_, status) => Dispatcher.UIThread.Post(() => OnUpdateStatus(status));

        _updateTimer = new Timer(
            _ => _ = CheckForUpdatesAsync(),
            null,
            TimeSpan.FromMinutes(2),
            VelopackUpdateService.CheckInterval);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_disposed)
        {
            return;
        }

        // CheckAsync swallows its own failures; this guards only against the app
        // shutting down mid-check, which would otherwise take the process with it
        // from a timer thread with no handler above it.
        try
        {
            await _updates.CheckAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Pushes a new update status into any open window.</summary>
    private void OnUpdateStatus(UpdateStatus status) => _detailsViewModel?.ApplyUpdate(status);

    /// <summary>Builds and starts a monitor for the current settings.</summary>
    private void StartMonitor()
    {
        _monitor?.Dispose();

        IUsageProvider provider = AppServices.CreateProvider(_services, _settings);
        _monitor = new UsageMonitor(
            provider,
            _settings.Polling,
            _services.GetRequiredService<TimeProvider>(),
            log: _services.GetRequiredService<ILogger<UsageMonitor>>());

        _monitor.Snapshots.Subscribe(new IndicatorObserver(this));
        _monitor.Start();

        // The details and report windows, if they exist, are bound to the old
        // monitor and would silently stop updating.
        _detailsViewModel?.Dispose();
        _detailsViewModel = null;
        _detailsWindow?.Close();
        _detailsWindow = null;

        _reportViewModel?.Dispose();
        _reportViewModel = null;
        _reportWindow?.Close();
        _reportWindow = null;
    }

    /// <summary>Redraws the tray icon for a reading.</summary>
    private void RenderIndicator(UsageSnapshot? snapshot)
    {
        ThresholdState state = ThresholdEvaluator.Evaluate(
            snapshot, _settings.IndicatorMode, _settings.ThresholdPercent);

        _indicator.Render(snapshot, _settings.IndicatorMode, state, CurrentAlert());
    }

    /// <summary>
    /// Works out whether the user has to do something.
    /// </summary>
    /// <remarks>
    /// Only a missing or rejected credential is actionable. A network failure or a
    /// 429 resolves itself, so it must not nag - and a 429 in particular proves
    /// nothing about the credential (see <c>docs/data-source.md</c>).
    /// </remarks>
    private IndicatorAlert CurrentAlert() => _monitor?.Status.Failure switch
    {
        UsageFetchFailure.NoCredential or UsageFetchFailure.Unauthorized =>
            IndicatorAlert.NeedsCredential,
        UsageFetchFailure.Network or UsageFetchFailure.ServerError or UsageFetchFailure.Unreadable =>
            IndicatorAlert.Unreachable,
        _ => IndicatorAlert.None,
    };

    /// <summary>
    /// A left click opens the details window - unless there is no credential, in
    /// which case it goes straight to Config.
    /// </summary>
    /// <remarks>
    /// Showing three dashes and a "no credential" line, then making the user find
    /// Config in a submenu, is a worse answer than taking them there.
    /// </remarks>
    private void OnLeftClicked()
    {
        if (CurrentAlert() == IndicatorAlert.NeedsCredential)
        {
            ShowConfig();
            return;
        }

        ToggleDetails();
    }

    /// <summary>
    /// Opens the popup, or closes it if the click was meant to dismiss it.
    /// </summary>
    /// <remarks>
    /// The timing rule lives in <see cref="TrayPopupToggle"/> and exists because
    /// the popup has already hidden itself by the time this runs - see the notes
    /// there.
    /// </remarks>
    private void ToggleDetails()
    {
        TimeSpan sinceDismissed =
            _services.GetRequiredService<TimeProvider>().GetUtcNow() - _detailsDismissedAt;

        switch (TrayPopupToggle.Decide(_detailsWindow?.IsVisible ?? false, sinceDismissed))
        {
            case PopupClickResult.Hide:
                _detailsWindow?.Hide();
                break;

            case PopupClickResult.Ignore:
                break;

            default:
                ShowDetails();
                break;
        }
    }

    private void OnMenuAction(object? sender, ContextActionEventArgs e)
    {
        switch (e.Action)
        {
            case ContextAction.ShowDetails:
                ShowDetails();
                break;

            case ContextAction.ShowReport:
                ShowReport();
                break;

            case ContextAction.Refresh:
                _ = RefreshAsync();
                break;

            case ContextAction.OpenConfig:
                ShowConfig();
                break;

            case ContextAction.ShowInfo:
                ShowInfo();
                break;

            case ContextAction.ChangeMode when e.Mode is { } mode:
                _ = ChangeModeAsync(mode);
                break;

            case ContextAction.Quit:
                _lifetime.Shutdown();
                break;

            default:
                break;
        }
    }

    private async Task RefreshAsync()
    {
        if (_monitor is not null)
        {
            await _monitor.RefreshNowAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Switches the displayed metric and remembers the choice.</summary>
    private async Task ChangeModeAsync(IndicatorMode mode)
    {
        _settings = _settings with { IndicatorMode = mode };
        RenderIndicator(_monitor?.Latest);
        await _configStore.SaveAsync(_settings).ConfigureAwait(true);
    }

    private void ShowDetails()
    {
        if (_monitor is null)
        {
            return;
        }

        if (_detailsViewModel is null)
        {
            _detailsViewModel = new DetailsViewModel(
                _monitor,
                () => _settings,
                _services.GetRequiredService<ILocalizer>(),
                _services.GetRequiredService<TimeProvider>());

            // The popup's "More" button routes through here rather than opening a
            // window itself, so the report is reused if it is already open.
            _detailsViewModel.ReportRequested += (_, _) => ShowReport();

            // Same reason: the empty state's "Open Config" must reuse an already
            // open Config window rather than opening a second one.
            _detailsViewModel.ConfigRequested += (_, _) => ShowConfig();

            // Restarting into a staged update is the controller's business too:
            // the view model must not know that applying an update ends the process.
            _detailsViewModel.UpdateRequested += (_, _) => _updates.ApplyAndRestart();
        }

        if (_detailsWindow is null)
        {
            _detailsWindow = new DetailsWindow { DataContext = _detailsViewModel };

            // The height only becomes known once the content has been measured, and
            // it changes again when the text inside does ("resets in 2h" versus a
            // "stale" badge appearing). Repositioning on every size change keeps the
            // window anchored to the tray corner instead of growing off the screen.
            _detailsWindow.SizeChanged += (_, _) => PositionNearTray(_detailsWindow!);

            // The window hides itself on deactivation; this records when, so a
            // click on the tray icon that caused it does not reopen the window.
            _detailsWindow.Deactivated += (_, _) =>
                _detailsDismissedAt = _services.GetRequiredService<TimeProvider>().GetUtcNow();
        }

        _detailsViewModel.Apply(_monitor.Latest);
        _detailsViewModel.ApplyUpdate(_updates.Status);
        PositionNearTray(_detailsWindow);
        ShowWindow(_detailsWindow);
    }

    /// <summary>
    /// Opens the full report window.
    /// </summary>
    /// <remarks>
    /// Created once and hidden rather than closed, like the other windows, so its
    /// subscription to the monitor survives and reopening is instant. It is
    /// <b>not</b> positioned near the tray: it is a reference window, not an
    /// overlay, so the OS default of centred is right.
    /// </remarks>
    private void ShowReport()
    {
        if (_monitor is null)
        {
            return;
        }

        if (_reportWindow is null)
        {
            _reportViewModel = new ReportViewModel(
                _monitor,
                AppServices.CreateProvider(_services, _settings),
                _services.GetRequiredService<IPlatformInfo>(),
                _services.GetRequiredService<ILocalizer>(),
                _services.GetRequiredService<TimeProvider>());

            _reportWindow = new ReportWindow { DataContext = _reportViewModel };
            _reportWindow.Closing += (_, args) =>
            {
                args.Cancel = true;
                _reportWindow?.Hide();
            };
        }

        _reportViewModel?.Apply(_monitor.Latest);
        ShowWindow(_reportWindow);
    }

    private void ShowConfig()
    {
        if (_configWindow is null)
        {
            var viewModel = new ConfigViewModel(
                _services.GetRequiredService<CredentialService>(),
                _services.GetRequiredService<IAutostart>(),
                _services.GetRequiredService<IPlatformInfo>(),
                _services.GetRequiredService<ITaskbarHost>(),
                _configStore,
                _services.GetRequiredService<Localizer>(),
                _services.GetRequiredService<JsonLanguageStore>(),
                _services.GetRequiredService<JsonThemeStore>(),
                () => _services.GetRequiredService<ITrayThemeProvider>().Current != TrayBackground.Light,
                () => _settings,
                ApplySettingsAsync);

            _configWindow = new ConfigWindow { DataContext = viewModel };
            _configWindow.Closing += (_, args) =>
            {
                // Hide rather than close: the view model holds a live credential
                // service and rebuilding it on every open is pointless work.
                args.Cancel = true;
                _configWindow?.Hide();
            };

            _ = viewModel.LoadAsync();
        }

        ShowWindow(_configWindow);
    }

    private void ShowInfo()
    {
        if (_infoWindow is null)
        {
            IUsageProvider provider = AppServices.CreateProvider(_services, _settings);
            _infoWindow = new InfoWindow
            {
                DataContext = new InfoViewModel(
                    _services.GetRequiredService<IPlatformInfo>(),
                    provider,
                    _services.GetRequiredService<ILocalizer>()),
            };

            _infoWindow.Closing += (_, args) =>
            {
                args.Cancel = true;
                _infoWindow?.Hide();
            };
        }

        ShowWindow(_infoWindow);
    }

    /// <summary>Redraws, and caches a fresh reading for the next launch.</summary>
    private void OnSnapshot(UsageSnapshot snapshot)
    {
        RenderIndicator(snapshot);
        TrackVelocity(snapshot);

        // Only cache a genuinely fresh reading. Re-saving a stale one on every
        // failed poll would keep resetting its age and defeat the staleness cap.
        if (!snapshot.IsStale)
        {
            // Fire and forget, but observe the fault. A bare "_ =" here is exactly
            // how the cache once died silently: the task faulted, nothing awaited
            // it, and no line was ever logged.
            _ = _snapshotCache.SaveAsync(snapshot).ContinueWith(
                task => _services.GetRequiredService<ILogger<TrayApplicationController>>()
                    .LogWarning(task.Exception, "Caching the latest reading failed."),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Feeds the reading into the velocity histories and warns if usage is
    /// climbing fast enough to run out before a window resets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule is in Core (<see cref="VelocityRule"/>); this decides what to do
    /// with its answer. The details popup carries the warning as a banner for as
    /// long as it holds. The indicator is asked to show it once per window per
    /// <see cref="VelocityRenotify"/>, so a pace that stays high does not open the
    /// hover card on every poll - the banner is there for anyone who missed it.
    /// </para>
    /// <para>
    /// Only fresh readings count. A stale one is a cached number with an old
    /// timestamp, and a rate measured against it would be about a different day.
    /// </para>
    /// </remarks>
    private void TrackVelocity(UsageSnapshot snapshot)
    {
        if (snapshot.IsStale)
        {
            return;
        }

        DateTimeOffset now = _services.GetRequiredService<TimeProvider>().GetUtcNow();
        ILocalizer localizer = _services.GetRequiredService<ILocalizer>();

        VelocityAlert? worst = null;
        foreach ((VelocityWindow kind, UsageWindow? window, UsageHistory history) in new[]
        {
            (VelocityWindow.Session, snapshot.Session, _sessionHistory),
            (VelocityWindow.Week, snapshot.Week, _weekHistory),
        })
        {
            if (window is null)
            {
                history.Clear();
                continue;
            }

            history.Add(new UsageSample(snapshot.FetchedAt, window.Percent));

            if (!_settings.VelocityAlerts)
            {
                continue;
            }

            VelocityAlert? alert = VelocityRule.Evaluate(kind, history.Samples, window.ResetsAt, now);

            // Keep the one that runs out soonest, the most urgent thing to say.
            if (alert is not null && (worst is null || alert.UntilExhausted < worst.UntilExhausted))
            {
                worst = alert;
            }
        }

        string message = worst is null ? string.Empty : DescribeVelocity(localizer, worst, now);

        // The banner holds for as long as the pace does; it is there for anyone
        // who opens the popup after the flash.
        if (_detailsViewModel is not null)
        {
            _detailsViewModel.VelocityText = message;
        }

        if (worst is null)
        {
            // Pace fell back to safe: the next genuine alert should flash at once.
            _lastVelocityNotice = DateTimeOffset.MinValue;
            return;
        }

        // Flash the indicator once, then stay quiet for a while even if the pace
        // holds. The banner keeps saying it; the interruption does not repeat.
        if (now - _lastVelocityNotice >= VelocityRenotify)
        {
            _indicator.ShowNotice(message, VelocityNoticeDuration);
            _lastVelocityNotice = now;
        }
    }

    /// <summary>Composes the velocity warning sentence for the worst window.</summary>
    private static string DescribeVelocity(ILocalizer localizer, VelocityAlert alert, DateTimeOffset now)
    {
        string window = localizer[alert.Window == VelocityWindow.Session
            ? "Velocity_Window_Session"
            : "Velocity_Window_Week"];
        string untilExhausted = DetailsViewModel.DescribeAge(localizer, alert.UntilExhausted);
        string untilReset = DetailsViewModel.DescribeAge(localizer, alert.UntilReset);
        return localizer.Format("Velocity_Alert", window, untilExhausted, untilReset);
    }

    /// <summary>Applies settings saved from the Config window.</summary>
    private async Task ApplySettingsAsync(AppSettings settings)
    {
        bool needsRestart =
            settings.UseFakeProvider != _settings.UseFakeProvider
            || settings.CredentialSource != _settings.CredentialSource
            || settings.Polling.BaseInterval != _settings.Polling.BaseInterval;

        bool settingsChangedUpdates = settings.AutomaticUpdates != _settings.AutomaticUpdates;
        bool indicatorChanged = settings.Indicator != _settings.Indicator;

        _settings = settings;

        if (indicatorChanged)
        {
            SwitchIndicator(settings.Indicator);
        }

        ApplyLanguage();
        ApplyTheme();
        await _configStore.SaveAsync(settings).ConfigureAwait(true);

        if (needsRestart)
        {
            StartMonitor();
        }

        if (settingsChangedUpdates)
        {
            StartUpdates();
        }

        RenderIndicator(_monitor?.Latest);
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Puts the details window near the tray, fully on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The size passed to <see cref="TrayPopupPlacement"/> is the laid-out size when
    /// there is one. There is not on the very first open: the window declares
    /// <c>SizeToContent="Height"</c>, so its height is unknown until it has been
    /// measured. That first placement therefore uses an estimate, and the
    /// <c>SizeChanged</c> hook in <see cref="ShowDetails"/> corrects it as soon as
    /// the real size exists.
    /// </para>
    /// <para>
    /// Recomputed on every open rather than once at construction, so moving the
    /// taskbar, changing the resolution or unplugging a monitor cannot leave the
    /// popup stranded where the screen used to be.
    /// </para>
    /// </remarks>
    private static void PositionNearTray(Window window)
    {
        IReadOnlyList<Screen> all = window.Screens.All;
        Screen? screen = window.Screens.Primary ?? (all.Count > 0 ? all[0] : null);
        if (screen is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        Size laidOut = window.Bounds.Size;
        var size = new Size(
            laidOut.Width > 0 ? laidOut.Width : window.Width,
            laidOut.Height > 0 ? laidOut.Height : window.Height);

        window.Position = TrayPopupPlacement.Place(
            screen.Bounds, screen.WorkingArea, size, screen.Scaling);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _updateTimer?.Dispose();
        _updates.Dispose();
        _monitor?.Dispose();
        _detailsViewModel?.Dispose();
        _reportViewModel?.Dispose();
        _indicator.Dispose();
    }

    /// <summary>Keeps the tray icon in step with the monitor, on the UI thread.</summary>
    private sealed class IndicatorObserver(TrayApplicationController owner) : IObserver<UsageSnapshot>
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
                owner.OnSnapshot(value);
            }
            else
            {
                Dispatcher.UIThread.Post(() => owner.OnSnapshot(value));
            }
        }
    }
}
