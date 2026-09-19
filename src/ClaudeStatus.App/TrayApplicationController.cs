using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using ClaudeStatus.App.Composition;
using ClaudeStatus.App.Sessions;
using ClaudeStatus.App.Theming;
using ClaudeStatus.App.Tray;
using ClaudeStatus.App.Update;
using ClaudeStatus.App.ViewModels;
using ClaudeStatus.App.Views;
using ClaudeStatus.Config;
using ClaudeStatus.Localization;
using ClaudeStatus.Platform;
using ClaudeStatus.Security;
using ClaudeStatus.Sessions;
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

    /// <summary>Recent readings and the pace verdict drawn from them.</summary>
    private readonly VelocityTracker _velocity = new();

    /// <summary>Which Claude Code sessions are running. Null while the feature is off.</summary>
    private SessionWatcher? _sessions;

    /// <summary>How long a session notice stays up.</summary>
    /// <remarks>Shorter than the velocity warning: it is news, not a diagnosis.</remarks>
    private static readonly TimeSpan SessionNoticeDuration = TimeSpan.FromSeconds(6);

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

    /// <summary>
    /// Where the pointer was when the popup was last asked for, in the OS's own
    /// screen units, or null where the platform cannot say.
    /// </summary>
    /// <remarks>
    /// Stands in for the tray icon's position, which no backend reports. Captured
    /// at the click rather than read at placement time - see
    /// <see cref="ITrayPointerLocator"/> for why that distinction is the whole
    /// point of it.
    /// </remarks>
    private double? _pointerXAtClick;

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

        // Where the OS draws a status item from text, that is the best indicator
        // available: it sizes itself to the reading and can tell a left click from
        // a right one, neither of which a rendered icon can do.
        if (services.GetRequiredService<IPlatformInfo>().SupportsInlineTrayText
            && PlatformServices.CreateNativeStatusItem(
                services.GetService<ILoggerFactory>()) is { IsAvailable: true } native)
        {
            return new NativeStatusIndicator(
                native,
                services.GetRequiredService<ILocalizer>(),
                services.GetService<ILogger<NativeStatusIndicator>>(),
                services.GetRequiredService<TimeProvider>());
        }

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
            services.GetRequiredService<ITrayThemeProvider>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IPlatformInfo>().TrayIsAtTop);
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
        => new(
            settings.ThresholdPercent,
            settings.ShowFableInWidget,
            settings.WidgetFollowsSystem,
            settings.Polling?.BaseInterval);

    /// <summary>The settings currently in force.</summary>
    public AppSettings Settings => _settings;

    /// <summary>Loads settings, starts polling, and shows the tray icon.</summary>
    public async Task StartAsync()
    {
        _settings = await _configStore.LoadAsync().ConfigureAwait(true);

        // Before any window exists. The app lives in the tray or the menu bar and
        // quits from its own menu, so a Dock icon offers a way in that leads
        // nowhere and a way out that skips the menu.
        if (_services.GetRequiredService<IAppPresentation>().HideFromDock())
        {
            _services.GetRequiredService<ILogger<TrayApplicationController>>()
                .LogInformation("Running as a background app: no Dock or task switcher entry.");
        }

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

        _services.GetRequiredService<ITrayThemeProvider>().Changed += OnTaskbarThemeChanged;

        _services.GetRequiredService<ILogger<TrayApplicationController>>().LogInformation(
            "ClaudeStatus starting. Source: {Source}. Poll interval: {Interval}.",
            _settings.CredentialSource,
            _settings.Polling.BaseInterval);

        await EnforceAutostartAsync().ConfigureAwait(true);
        await StartSessionWatchAsync().ConfigureAwait(true);

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
    /// Reading it fresh here, and running again on <see cref="ITrayThemeProvider.Changed"/>,
    /// means a theme change on the OS is picked up as it happens.
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

        // Normalized() has already resolved the null, but these settings can also
        // arrive straight from a caller, so the default is restated rather than
        // assumed.
        ThemeApplier.Apply(
            application,
            theme,
            _settings.FontFamily,
            _settings.OsdTransparency ?? AppSettings.DefaultOsdTransparency);

        // Runs on every settings change, which is exactly when these can move.
        _indicator.Configure(IndicatorOptionsFrom(_settings));
    }

    /// <summary>
    /// Redraws everything drawn against the taskbar the moment it changes colour.
    /// </summary>
    /// <remarks>
    /// Both indicators and the "system" theme already read the provider on every
    /// render; this only makes that render happen now instead of at the next poll.
    /// The provider raises it off the UI thread.
    /// </remarks>
    private void OnTaskbarThemeChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            ApplyTheme();
            RenderIndicator(_monitor?.Latest);
        });

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
    /// <remarks>
    /// The row is the one mode whose verdict is not a single window's, so it is
    /// judged separately - see <see cref="ThresholdEvaluator.EvaluateRow"/>. The
    /// branch is on the mode, not on the operating system, so it stays inside the
    /// rule <c>docs/manual.md</c> §6 sets.
    /// </remarks>
    private void RenderIndicator(UsageSnapshot? snapshot)
    {
        IndicatorMode mode = EffectiveMode;

        ThresholdState state = mode == IndicatorMode.Row
            ? ThresholdEvaluator.EvaluateRow(
                snapshot, _settings.ThresholdPercent, _settings.ShowFableInWidget)
            : ThresholdEvaluator.Evaluate(snapshot, mode, _settings.ThresholdPercent);

        _indicator.Render(snapshot, mode, state, CurrentAlert());
    }

    /// <summary>
    /// The mode this machine can actually draw.
    /// </summary>
    /// <remarks>
    /// The config file travels between machines - it is the same account and the
    /// same sync folder - so a mode chosen on a Mac can be read by a Windows
    /// install that has no way to render it. Resolving it here means the setting
    /// survives the round trip instead of being rewritten to something else the
    /// moment the other machine starts.
    /// </remarks>
    private IndicatorMode EffectiveMode
        => _settings.IndicatorMode == IndicatorMode.Row
            && !_services.GetRequiredService<IPlatformInfo>().SupportsInlineTrayText
            ? IndicatorMode.SessionPercent
            : _settings.IndicatorMode;

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
        // Read now, not when the window is placed. By then the popup has been
        // constructed and measured and the pointer may have moved on; this is the
        // one instant it is guaranteed to be over the tray icon.
        _pointerXAtClick = _services.GetRequiredService<ITrayPointerLocator>().PointerX;

        _services.GetRequiredService<ILogger<TrayApplicationController>>().LogInformation(
            "Indicator left click. pointerX={PointerX} alert={Alert}",
            _pointerXAtClick,
            CurrentAlert());

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
        // The context menu opens at the icon and the pointer is still on it, so
        // this is as good an anchor as the left click's. Captured for every action
        // rather than just Details so a stale position from an earlier click can
        // never be the one that places the window.
        _pointerXAtClick = _services.GetRequiredService<ITrayPointerLocator>().PointerX;

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

    /// <summary>Silences or unsilences one session from the popup's switch, and remembers it.</summary>
    private async Task ChangeMuteAsync(string sessionId, bool muted)
    {
        IEnumerable<string> others = _settings.MutedSessions
            .Where(id => !string.Equals(id, sessionId, StringComparison.Ordinal));
        _settings = _settings with { MutedSessions = muted ? [.. others, sessionId] : [.. others] };
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

            // The popup's switches go through the same path as Config's Save, so
            // the watcher and our hooks follow the switch at once.
            _detailsViewModel.SessionWatchChanged += (_, on) =>
                _ = ApplySettingsAsync(_settings with { DisableSessionWatch = !on });

            _detailsViewModel.SessionMuteChanged += (_, e) => _ = ChangeMuteAsync(e.SessionId, e.IsMuted);
            _detailsViewModel.SessionFocusRequested += OnSessionFocusRequested;
        }

        _detailsViewModel.ApplySessionWatch(_settings.SessionWatch);

        // The view model is built the first time the popup is opened, which is
        // normally long after the watcher started, so the list has to be handed
        // over here as well as when a session changes.
        PushSessions();

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
            HideOnClose.Attach(_reportWindow);
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
                ApplySettingsAsync,
                _services.GetRequiredService<IHookManager>(),
                () => _sessions?.Sessions ?? []);

            _configWindow = new ConfigWindow { DataContext = viewModel };

            // Hide rather than close: the view model holds a live credential
            // service and rebuilding it on every open is pointless work.
            HideOnClose.Attach(_configWindow);
        }

        // On every open, not just the first: the popup's switches and the Show
        // menu change settings behind a hidden Config, and a form still showing
        // the old values would put them back on the next Save.
        if (!_configWindow.IsVisible && _configWindow.DataContext is ConfigViewModel config)
        {
            _ = config.LoadAsync();
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

            HideOnClose.Attach(_infoWindow);
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
    /// The decision is in Core (<see cref="VelocityTracker"/>, which owns the
    /// histories, the horizons and the latch); this decides what to do with its
    /// answer. The details popup carries the warning as a banner for as long as it
    /// holds. The indicator is asked to show it once per <see cref="VelocityRenotify"/>,
    /// so a pace that stays high does not open the card on every poll - the banner
    /// is there for anyone who missed it.
    /// </para>
    /// <para>
    /// Only fresh readings count, which the tracker enforces: a stale one is a
    /// cached number with an old timestamp, and a rate measured against it would
    /// be about a different day.
    /// </para>
    /// </remarks>
    private void TrackVelocity(UsageSnapshot snapshot)
    {
        DateTimeOffset now = _services.GetRequiredService<TimeProvider>().GetUtcNow();
        ILocalizer localizer = _services.GetRequiredService<ILocalizer>();

        VelocityAlert? worst = _velocity.Observe(snapshot, now, _settings.VelocityAlerts);
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
        bool sessionWatchChanged = settings.SessionWatch != _settings.SessionWatch;

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

        if (sessionWatchChanged)
        {
            // Both directions: StartSessionWatchAsync also stops the watcher and
            // removes our hooks when the setting is off.
            await StartSessionWatchAsync().ConfigureAwait(true);
            _detailsViewModel?.ApplySessionWatch(settings.SessionWatch);
            if (!settings.SessionWatch)
            {
                _indicator.ShowSessions([]);
            }
        }

        RenderIndicator(_monitor?.Latest);
    }

    /// <summary>
    /// Registers autostart when the settings ask for it and the OS has not got it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Autostart is the only setting whose truth lives outside our config file -
    /// it is a registry value, a LaunchAgent or a .desktop file - so the two can
    /// disagree. They do after an update that moves the executable, after a
    /// profile is copied to a new machine, and after anything else clears the
    /// entry. A tray app that does not come back at login is simply absent, and
    /// nobody goes looking in a settings window for a thing they never see, so
    /// the setting is re-asserted on every start rather than only when saved.
    /// </para>
    /// <para>
    /// It is re-asserted, never decided: <see cref="AppSettings.DisableAutostart"/>
    /// is what an explicit "no" is stored as, and this does nothing at all when
    /// that is set. A refusal is logged and dropped - the app has plenty to do
    /// without it, and interrupting a launch over a registry write nobody asked
    /// about would be worse than the missing entry.
    /// </para>
    /// </remarks>
    private async Task EnforceAutostartAsync()
    {
        ILogger<TrayApplicationController> log =
            _services.GetRequiredService<ILogger<TrayApplicationController>>();
        IAutostart autostart = _services.GetRequiredService<IAutostart>();

        if (!_settings.StartWithOperatingSystem || !autostart.IsSupported)
        {
            return;
        }

        try
        {
            if (await autostart.IsEnabledAsync().ConfigureAwait(true))
            {
                return;
            }

            await autostart.SetEnabledAsync(true).ConfigureAwait(true);
            log.LogInformation("Autostart was missing and has been registered.");
        }
        catch (AutostartException ex)
        {
            log.LogWarning(ex, "Could not register autostart. Carrying on without it.");
        }
    }

    /// <summary>
    /// Starts watching Claude Code sessions, installing our hooks to do it.
    /// </summary>
    /// <remarks>
    /// The hooks exist only while this app does - see <see cref="SessionWatcher"/>.
    /// A failure here is logged and dropped: it means no session list, which is
    /// one feature missing, not a reason to refuse to start a usage meter.
    /// </remarks>
    private async Task StartSessionWatchAsync()
    {
        if (!_settings.SessionWatch)
        {
            // Not merely "do not listen": if the feature was on last run, our
            // hooks are still in Claude Code's settings and have to come out.
            await StopSessionWatchAsync().ConfigureAwait(true);
            await _services.GetRequiredService<IHookManager>()
                .SyncAsync(enabled: false).ConfigureAwait(true);
            return;
        }

        if (_sessions is not null)
        {
            return;
        }

        _sessions = new SessionWatcher(
            _services.GetRequiredService<IHookManager>(),
            _services.GetRequiredService<SessionSpool>(),
            new SessionRegistry(_settings.SessionRetention),
            _services.GetRequiredService<SessionStore>(),
            _services.GetRequiredService<TimeProvider>(),
            _services.GetService<ILogger<SessionWatcher>>());

        _sessions.Changed += OnSessionChanged;

        // Resolved here, on the UI thread, rather than at the first toast: the
        // Windows notifier's window receives the click, and only a thread that
        // pumps messages ever delivers it.
        _services.GetRequiredService<INotifier>().Activated += OnNotificationActivated;

        await _sessions.StartAsync().ConfigureAwait(true);

        // Whatever was already spooled is on the list before the card can be
        // opened, so the first hover is not an empty one.
        PushSessions();
    }

    /// <summary>Hands the current session list to everything that shows one.</summary>
    /// <remarks>
    /// Both surfaces, always. The hover card is the glance and the details window
    /// is the one every platform can open - macOS has no widget to hover, so
    /// pushing only to the indicator would make this a Windows feature.
    /// </remarks>
    private void PushSessions()
    {
        if (_sessions is not { } watcher)
        {
            return;
        }

        IReadOnlyList<ClaudeSession> sessions = watcher.Sessions;
        _indicator.ShowSessions(sessions);

        if (_detailsViewModel is { } details)
        {
            details.ShowSessions = true;
            details.ApplySessions(sessions, _services.GetRequiredService<TimeProvider>().GetUtcNow());
        }
    }

    /// <summary>Stops watching and removes our hooks.</summary>
    private async Task StopSessionWatchAsync()
    {
        if (_sessions is null)
        {
            return;
        }

        SessionWatcher watcher = _sessions;
        _sessions = null;
        watcher.Changed -= OnSessionChanged;
        _services.GetRequiredService<INotifier>().Activated -= OnNotificationActivated;

        await watcher.StopAsync().ConfigureAwait(true);
        watcher.Dispose();
    }

    /// <summary>
    /// Says a session has finished, unless the user has silenced that one or is
    /// looking at its terminal.
    /// </summary>
    /// <remarks>
    /// Only <see cref="SessionChange.Idle"/> and <see cref="SessionChange.Finished"/>
    /// are worth an interruption. A session appearing is not news - the user
    /// started it - and it would fire the moment the app launched, once per
    /// session already open.
    /// </remarks>
    private void OnSessionChanged(object? sender, SessionChangedEventArgs e)
    {
        // The spool is drained on a worker thread, and everything below this
        // touches windows.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSessionChanged(sender, e));
            return;
        }

        // Every change updates the list, even the ones not worth interrupting for.
        PushSessions();

        // A session is proof that Claude Code is logged in - it is usually what
        // just wrote the credential. Without this the indicator keeps saying "no
        // credential" beside a live session count until the backed-off poll comes
        // round. The monitor's forced-refresh cooldown keeps a burst of events to
        // one fetch.
        if (CurrentAlert() == IndicatorAlert.NeedsCredential)
        {
            _ = RefreshAsync();
        }

        if (e.Change is not (SessionChange.Idle or SessionChange.Finished))
        {
            return;
        }

        if (_settings.MutedSessions.Contains(e.Session.Id, StringComparer.Ordinal))
        {
            return;
        }

        // Only for someone who has looked away. A turn that ends in milliseconds
        // (/clear) otherwise announces itself to a user still looking at it. The
        // spool is read within a fraction of a second of the hook, so "now" is
        // close enough to "when it finished". A session with no known window is
        // announced, as before.
        if (e.Session.Origin is { } origin
            && _services.GetRequiredService<ITerminalFocus>().IsForeground(origin))
        {
            _services.GetRequiredService<ILogger<TrayApplicationController>>().LogInformation(
                "Session {Change} for {Folder}: not notified, its terminal has focus.", e.Change, e.Session.Folder);
            return;
        }

        ILocalizer localizer = _services.GetRequiredService<ILocalizer>();
        string name = e.Session.Name.Length > 0 ? e.Session.Name : localizer["Sessions_Unnamed"];
        string message = localizer.Format(
            e.Change == SessionChange.Finished ? "Sessions_Notice_Ended" : "Sessions_Notice_Idle",
            name);

        // A real OS notification where there is one, and the app's own card only
        // where there is not. The card is right for something the user is already
        // looking at; this fires when a session they walked away from has finished,
        // so it has to survive being missed - which a card that fades after six
        // seconds does not. The card also reads as the hover card appearing for no
        // reason, which is worse than saying nothing.
        INotifier notifier = _services.GetRequiredService<INotifier>();
        bool shown = notifier.Notify(localizer["Sessions_Heading"], message, e.Session.Id);

        // Which notifier, and whether the OS took it: a toast that never appears
        // leaves nothing else behind to tell an installed-app problem from a
        // missing registration.
        _services.GetRequiredService<ILogger<TrayApplicationController>>().LogInformation(
            "Session {Change} for {Folder}: {Notifier} {Result}.",
            e.Change,
            e.Session.Folder,
            notifier.GetType().Name,
            shown ? "accepted it" : "refused it, showing the card");

        if (!shown)
        {
            _indicator.ShowNotice(message, SessionNoticeDuration);
        }
    }

    /// <summary>
    /// Takes the user to the session a clicked notification was about.
    /// </summary>
    /// <remarks>
    /// Runs synchronously inside the click, and must: Windows grants the right to
    /// put another process's window in front only for as long as the click is the
    /// latest input, and a <c>Dispatcher.Post</c> would spend it. A toast's click
    /// arrives on a thread-pool thread, so it is carried over with a blocking
    /// <c>Invoke</c> instead. Where the session's window is not known - it predates
    /// this feature, or the hook could not find one - the details window opens
    /// instead, which lists it.
    /// </remarks>
    private void OnNotificationActivated(object? sender, NotificationActivatedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Invoke(() => OnNotificationActivated(sender, e));
            return;
        }

        if (FindSession(e.Tag) is not { Origin: not null } session)
        {
            ShowDetails();
            return;
        }

        FocusTerminal(session, "Notification clicked");
    }

    /// <summary>
    /// Takes the user to the session they clicked in the details window's list.
    /// </summary>
    /// <remarks>
    /// Inside the click, like a notification's, so Windows still counts it as the
    /// latest input. A row is only clickable when its terminal is known; the check
    /// is repeated because the list can be a moment older than the watcher.
    /// </remarks>
    private void OnSessionFocusRequested(object? sender, string sessionId)
    {
        if (FindSession(sessionId) is { Origin: not null } session)
        {
            FocusTerminal(session, "Session row clicked");
        }
    }

    private ClaudeSession? FindSession(string? id)
        => id is not null && _sessions is { } watcher
            ? watcher.Sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal))
            : null;

    /// <summary>Brings a session's terminal forward and logs how that went.</summary>
    /// <param name="session">A session whose origin is known.</param>
    /// <param name="source">What asked, for the log.</param>
    private void FocusTerminal(ClaudeSession session, string source)
    {
        SessionOrigin origin = session.Origin!;
        bool focused = _services.GetRequiredService<ITerminalFocus>().TryFocus(origin);

        _services.GetRequiredService<ILogger<TrayApplicationController>>().LogInformation(
            "{Source} for {Folder}: {Result} ({Precision}).",
            source,
            session.Folder,
            focused ? "focused its window" : "window not focused",
            origin.Precision);
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
    private void PositionNearTray(Window window)
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

        IPlatformInfo platform = _services.GetRequiredService<IPlatformInfo>();

        // The locator reports the OS's own screen units; everything in
        // TrayPopupPlacement is physical pixels, and on a HiDPI display those are
        // not the same number. Converting here keeps that conversion in the one
        // place that already knows the scaling.
        int? anchorX = _pointerXAtClick is { } pointer && double.IsFinite(pointer)
            ? (int)Math.Round(pointer * screen.Scaling)
            : null;

        window.Position = TrayPopupPlacement.Place(
            screen.Bounds,
            screen.WorkingArea,
            size,
            screen.Scaling,
            platform.TrayIsAtTop,
            anchorX);

        // A popup nobody can find looks exactly like a click that did nothing, and
        // the two have completely different causes. These are the numbers that tell
        // them apart.
        _services.GetRequiredService<ILogger<TrayApplicationController>>().LogInformation(
            "Popup placed at {Position}. size={Size} screen={Bounds} work={Work} "
            + "scaling={Scaling} anchorX={AnchorX} atTop={AtTop}",
            window.Position,
            size,
            screen.Bounds,
            screen.WorkingArea,
            screen.Scaling,
            anchorX,
            platform.TrayIsAtTop);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Synchronous, and deliberately so: this is the last chance to take our
        // hooks out of Claude Code's settings, and a fire-and-forget task here
        // races the process exit. Leave them in and every turn of every session
        // launches this executable for an app that is no longer running.
        if (_sessions is { } sessions)
        {
            _sessions = null;
            sessions.Changed -= OnSessionChanged;
            _services.GetRequiredService<INotifier>().Activated -= OnNotificationActivated;
            try
            {
                sessions.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing useful to do on the way out; the next start cleans up.
            }

            sessions.Dispose();
        }

        _services.GetRequiredService<ITrayThemeProvider>().Changed -= OnTaskbarThemeChanged;
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
