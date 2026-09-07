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
    private readonly TrayIcon _trayIcon;
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

    public TrayIconIndicator(ILocalizer localizer, ITrayThemeProvider? theme = null)
    {
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));

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
        _trayIcon.Menu = BuildMenu();

        // A NativeMenu is handed to the OS, and the backends differ on whether an
        // item's header can be changed after that. Rebuilding the whole menu is the
        // only thing that behaves the same everywhere, and it happens once per
        // language change, not per poll.
        _l.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(RebuildMenu);
    }

    /// <inheritdoc />
    public event EventHandler? LeftClicked;

    /// <inheritdoc />
    public event EventHandler<ContextActionEventArgs>? MenuAction;

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
        RenderTargetBitmap bitmap = TrayIconRenderer.Render(
            snapshot, mode, state, alert, _theme.Current);

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

    /// <summary>Builds the right-click menu described in <c>docs/manual.md</c> §3.</summary>
    private NativeMenu BuildMenu()
    {
        var showMenu = new NativeMenu();
        foreach (NativeMenuItem item in _modeItems)
        {
            showMenu.Add(item);
        }

        var menu = new NativeMenu
        {
            CreateItem("Tray_Details", ContextAction.ShowDetails),
            CreateItem("Tray_Report", ContextAction.ShowReport),
            new NativeMenuItem(_l["Tray_Show"]) { Menu = showMenu },
            CreateItem("Tray_Refresh", ContextAction.Refresh),
            new NativeMenuItemSeparator(),
            CreateItem("Tray_Config", ContextAction.OpenConfig),
            CreateItem("Tray_Info", ContextAction.ShowInfo),
            new NativeMenuItemSeparator(),
            CreateItem("Tray_Quit", ContextAction.Quit),
        };

        return menu;
    }

    /// <summary>Rebuilds the menu in the current language, keeping the mode tick.</summary>
    private void RebuildMenu()
    {
        if (_disposed)
        {
            return;
        }

        _modeItems = CreateModeItems();
        _trayIcon.Menu = BuildMenu();
        UpdateModeChecks(_currentMode);
    }

    /// <summary>The four mode items, in <see cref="IndicatorMode"/> order.</summary>
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
        for (int index = 0; index < _modeItems.Length; index++)
        {
            _modeItems[index].IsChecked = (IndicatorMode)index == mode;
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
