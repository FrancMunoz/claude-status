using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClaudeStatus.Localization;
using ClaudeStatus.Platform;
using ClaudeStatus.Theming;
using ClaudeStatus.Usage;

namespace ClaudeStatus.App.Tray;

/// <summary>
/// Shows usage in the system tray using Avalonia's <see cref="TrayIcon"/>.
/// </summary>
/// <remarks>
/// <para>
/// The icon is a bitmap rendered on every update by <see cref="TrayIconRenderer"/>,
/// so the previous one has to be disposed each time - this runs once a minute for
/// as long as the machine is on.
/// </para>
/// <para>
/// Click behaviour differs by platform and we do not fight it: on Windows a left
/// click fires <see cref="TrayIcon.Command"/> and a right click opens the menu; on
/// macOS a click opens the menu, so Details is also the first menu item.
/// </para>
/// </remarks>
public sealed class TrayIconIndicator : IStatusIndicator
{
    private readonly ITrayThemeProvider _theme;
    private readonly ILocalizer _l;
    private readonly TimeProvider _clock;
    private readonly TrayIcon _trayIcon;

    /// <summary>How old a reading may be before the icon fades it.</summary>
    /// <remarks>
    /// From <see cref="StalePolicy"/>, the same rule the macOS menu bar and the
    /// taskbar widget use. Until this existed the icon faded on the monitor's raw
    /// flag, which fires on one failed request - and against an endpoint that 429s
    /// readily that meant a faded icon over a reading seconds old.
    /// </remarks>
    private TimeSpan _staleAfter = StalePolicy.Floor;

    /// <summary>The context menu, created once and never replaced.</summary>
    /// <remarks>
    /// macOS's native exporter caches the <see cref="NativeMenu"/> it handed to the
    /// tray icon and rejects any other instance ("The menu being updated does not
    /// match"), so a language change refills this one in place rather than swapping
    /// it for a new one.
    /// </remarks>
    private readonly NativeMenu _menu = new();

    private RenderTargetBitmap? _currentBitmap;

    /// <summary>
    /// Replaced wholesale on a language change.
    /// </summary>
    /// <remarks>
    /// Not readonly, and the items are not reused: a <see cref="NativeMenuItem"/>
    /// belongs to exactly one <see cref="NativeMenu"/>, and adding one that already
    /// has a parent throws. Rebuilding the menu therefore has to rebuild the items.
    /// </remarks>
    private NativeMenuItem[] _modeItems;

    /// <summary>The mode last rendered, so a rebuilt menu keeps its radio tick.</summary>
    private IndicatorMode _currentMode = IndicatorMode.SessionPercent;


    private bool _disposed;

    /// <param name="localizer">Supplies the menu text.</param>
    /// <param name="theme">The tray background, for contrast. Unknown when omitted.</param>
    /// <param name="clock">Judges how old a reading is. The system clock by default.</param>
    public TrayIconIndicator(
        ILocalizer localizer, ITrayThemeProvider? theme = null, TimeProvider? clock = null)
    {
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _clock = clock ?? TimeProvider.System;

        // Unknown is the safe default: it makes the renderer draw a halo, which
        // reads on any panel colour.
        _theme = theme ?? new StaticTrayThemeProvider(TrayBackground.Unknown);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "ClaudeStatus",
            IsVisible = false,
            Command = new RelayCommandShim(() => LeftClicked?.Invoke(this, EventArgs.Empty)),
        };

        _modeItems = CreateModeItems();
        FillMenu();
        _trayIcon.Menu = _menu;

        // A NativeMenu is handed to the OS, and the backends differ on whether an
        // item's header can be changed after that. Refilling the menu with fresh
        // items is the only thing that behaves the same everywhere, and it happens
        // once per language change, not per poll.
        _l.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(RebuildMenu);
    }

    /// <inheritdoc />
    public event EventHandler? LeftClicked;

    /// <inheritdoc />
    public event EventHandler<ContextActionEventArgs>? MenuAction;

    /// <inheritdoc />
    /// <remarks>
    /// The icon shows one metric and needs none of the per-metric settings. It does
    /// need the poll interval, which is what tells it how old a reading has to be
    /// before fading it is honest rather than alarmist.
    /// </remarks>
    public void Configure(IndicatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _staleAfter = StalePolicy.ThresholdFor(options.PollInterval);
    }

    /// <inheritdoc />
    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _trayIcon.IsVisible = true;
    }

    /// <inheritdoc />
    public void Render(
        UsageSnapshot? snapshot, IndicatorMode mode, ThresholdState state, IndicatorAlert alert)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Dispatcher.UIThread.CheckAccess())
        {
            // The poll loop runs on a worker thread; touching TrayIcon off the UI
            // thread is undefined behaviour on every backend.
            Dispatcher.UIThread.Post(() => Render(snapshot, mode, state, alert));
            return;
        }

        // Read the theme on every render rather than caching it, so switching the
        // system theme takes effect on the next poll with no notification plumbing.
        //
        // Row is not drawable here and never reaches this tray by choice: an
        // Avalonia status item is square on every backend, so the row belongs to
        // the native indicator. It is mapped rather than rejected because the
        // config file travels between machines.
        RenderTargetBitmap bitmap = TrayIconRenderer.Render(
            snapshot,
            mode == IndicatorMode.Row ? IndicatorMode.SessionPercent : mode,
            state,
            alert,
            _theme.Current,
            StalePolicy.ShowsAsStale(snapshot, _clock.GetUtcNow(), _staleAfter));

        // Assign the new icon before disposing the old one; the backend may still
        // be reading the previous bitmap while it swaps.
        RenderTargetBitmap? previous = _currentBitmap;
        _currentBitmap = bitmap;
        _trayIcon.Icon = new WindowIcon(bitmap);
        previous?.Dispose();

        // macOS renders a template icon as a monochrome mask that follows the menu
        // bar's light or dark appearance. That is what we want normally, but it
        // would also throw away the red - so templating is off exactly when the
        // threshold is exceeded (docs/manual.md §3).
        bool wantsAttention = state == ThresholdState.Exceeded || alert == IndicatorAlert.NeedsCredential;
        MacOSProperties.SetIsTemplateIcon(_trayIcon, !wantsAttention);

        _trayIcon.ToolTipText = BuildTooltip(_l, snapshot, mode, alert);
        _currentMode = mode;
        UpdateModeChecks(mode);
    }

    /// <summary>Fills <see cref="_menu"/> with the menu described in <c>docs/manual.md</c> §3.</summary>
    private void FillMenu()
    {
        var showMenu = new NativeMenu();
        foreach (NativeMenuItem item in _modeItems)
        {
            showMenu.Add(item);
        }

        // Drop the old items before adding the new ones: a NativeMenuItem belongs to
        // exactly one NativeMenu, and adding one that still has a parent throws.
        _menu.Items.Clear();

        _menu.Add(CreateItem("Tray_Details", ContextAction.ShowDetails));
        _menu.Add(CreateItem("Tray_Report", ContextAction.ShowReport));
        _menu.Add(new NativeMenuItem(_l["Tray_Show"]) { Menu = showMenu });
        _menu.Add(CreateItem("Tray_Refresh", ContextAction.Refresh));
        _menu.Add(new NativeMenuItemSeparator());
        _menu.Add(CreateItem("Tray_Config", ContextAction.OpenConfig));
        _menu.Add(CreateItem("Tray_Info", ContextAction.ShowInfo));
        _menu.Add(new NativeMenuItemSeparator());
        _menu.Add(CreateItem("Tray_Quit", ContextAction.Quit));
    }

    /// <summary>Rebuilds the menu in the current language, keeping the mode tick.</summary>
    private void RebuildMenu()
    {
        if (_disposed)
        {
            return;
        }

        _modeItems = CreateModeItems();
        FillMenu();
        UpdateModeChecks(_currentMode);
    }

    /// <summary>
    /// The four mode items, in <see cref="IndicatorMode"/> order.
    /// </summary>
    /// <remarks>
    /// The row is not among them. It cannot be drawn into a square status item, so
    /// offering it here would be a menu entry that appears to do nothing.
    /// </remarks>
    private NativeMenuItem[] CreateModeItems() =>
    [
        CreateModeItem("Tray_Mode_Session", IndicatorMode.SessionPercent),
        CreateModeItem("Tray_Mode_Week", IndicatorMode.WeekPercent),
        CreateModeItem("Tray_Mode_WeekFable", IndicatorMode.WeekFablePercent),
        CreateModeItem("Tray_Mode_Ring", IndicatorMode.Ring),
    ];

    private NativeMenuItem CreateItem(string headerKey, ContextAction action)
    {
        var item = new NativeMenuItem(_l[headerKey])
        {
            Command = new RelayCommandShim(
                () => MenuAction?.Invoke(this, new ContextActionEventArgs(action))),
        };

        return item;
    }

    private NativeMenuItem CreateModeItem(string headerKey, IndicatorMode mode)
        => new(_l[headerKey])
        {
            ToggleType = MenuItemToggleType.Radio,
            Command = new RelayCommandShim(
                () => MenuAction?.Invoke(this, new ContextActionEventArgs(ContextAction.ChangeMode, mode))),
        };

    /// <summary>Moves the radio tick to the mode now on show.</summary>
    private void UpdateModeChecks(IndicatorMode mode)
    {
        // Row is drawn here as the session number, so that is where its tick goes;
        // the menu would otherwise show nothing selected at all.
        IndicatorMode ticked = mode == IndicatorMode.Row ? IndicatorMode.SessionPercent : mode;

        for (int index = 0; index < _modeItems.Length; index++)
        {
            _modeItems[index].IsChecked = (IndicatorMode)index == ticked;
        }
    }

    /// <summary>
    /// Builds the hover tooltip.
    /// </summary>
    /// <remarks>
    /// The tooltip is the only place all three numbers appear without opening a
    /// window, so it lists them all and says plainly when the data is stale.
    /// </remarks>
    internal static string BuildTooltip(
        ILocalizer localizer,
        UsageSnapshot? snapshot,
        IndicatorMode mode,
        IndicatorAlert alert = IndicatorAlert.None)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        if (alert == IndicatorAlert.NeedsCredential)
        {
            return localizer["Tray_Tooltip_NeedsCredential"];
        }

        if (snapshot is null)
        {
            return localizer[alert == IndicatorAlert.Unreachable
                ? "Tray_Tooltip_Unreachable"
                : "Tray_Tooltip_NoData"];
        }

        string session = Describe(localizer, snapshot.Session);
        string week = Describe(localizer, snapshot.Week);
        string fable = snapshot.WeekFable is null
            ? localizer["Common_NotAvailable"]
            : Describe(localizer, snapshot.WeekFable);

        string body = localizer.Format("Tray_Tooltip_Body", session, week, fable);
        return localizer.Format(
            snapshot.IsStale ? "Tray_Tooltip_Stale" : "Tray_Tooltip", body);
    }

    private static string Describe(ILocalizer localizer, UsageWindow? window)
        => window is null
            ? localizer["Common_Unknown"]
            : TrayIconRenderer.FormatPercent(window.Percent) + " %";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        _currentBitmap?.Dispose();
        _currentBitmap = null;
    }

    /// <summary>
    /// A minimal <see cref="System.Windows.Input.ICommand"/> for menu wiring.
    /// </summary>
    /// <remarks>
    /// The tray menu is built in code rather than AXAML because its items are
    /// generated from <see cref="IndicatorMode"/>, so there is no view model to
    /// bind a <c>[RelayCommand]</c> to. This keeps that wiring to one small type.
    /// </remarks>
    private sealed class RelayCommandShim(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
