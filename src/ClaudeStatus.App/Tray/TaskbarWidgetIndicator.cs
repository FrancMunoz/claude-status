using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ClaudeStatus.App.ViewModels;
using ClaudeStatus.App.Views;
using ClaudeStatus.Localization;
using ClaudeStatus.Platform;
using ClaudeStatus.Usage;
using Microsoft.Extensions.Logging;

namespace ClaudeStatus.App.Tray;

/// <summary>
/// Shows usage as a small card embedded in the taskbar itself.
/// </summary>
/// <remarks>
/// <para>
/// An Avalonia window, re-parented into the taskbar through
/// <see cref="ITaskbarHost"/> and kept in place by a one-second timer, because
/// Explorer relays out the taskbar whenever an icon appears in the tray and
/// restarts it outright now and then. The timer re-measures, re-attaches if the
/// taskbar has been recreated, and moves the window only when its slot changed.
/// </para>
/// <para>
/// Windows only for now. Everywhere else, and whenever the taskbar cannot be
/// joined, the app uses <see cref="TrayIconIndicator"/> instead - the two share
/// <see cref="IStatusIndicator"/>, so nothing upstream knows which it got.
/// </para>
/// </remarks>
public sealed class TaskbarWidgetIndicator : IStatusIndicator
{
    private readonly ITaskbarHost _host;
    private readonly ILocalizer _l;
    private readonly TimeProvider _clock;
    private readonly ITrayThemeProvider _taskbarTheme;
    private readonly ILogger _log;
    private readonly TaskbarWidgetViewModel _viewModel;
    private readonly TaskbarWidgetWindow _window;
    private readonly TaskbarHoverWindow _hover;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _hoverTimer;

    /// <summary>Takes the velocity notice back down after its duration.</summary>
    private readonly DispatcherTimer _noticeTimer;

    /// <summary>Gap between the widget and its hover card, in layout units.</summary>
    private const double HoverGap = 10d;

    /// <summary>
    /// Where the widget waits until it has a slot in the taskbar.
    /// </summary>
    /// <remarks>
    /// Far enough out to be off any monitor in any arrangement, and the value
    /// Windows itself uses for a window it does not want seen. Nothing is drawn
    /// there - the window is simply never composited onto a display.
    /// </remarks>
    private static readonly PixelPoint OffScreen = new(-32000, -32000);

    /// <summary>The taskbar as last measured, so the hover card can be placed from it.</summary>
    private TaskbarMetrics? _lastMetrics;

    /// <summary>Whether the hover card has been laid out at least once, so its Bounds mean something.</summary>
    private bool _hoverSized;

    private IndicatorMode _currentMode = IndicatorMode.SessionPercent;
    private UsageSnapshot? _lastSnapshot;
    private IndicatorAlert _lastAlert;
    private TaskbarSlot? _lastSlot;
    private bool _warnedNoSlot;
    private bool _warnedNoTaskbar;
    private bool _disposed;

    public TaskbarWidgetIndicator(
        ITaskbarHost host,
        ILocalizer localizer,
        TimeProvider clock,
        ITrayThemeProvider taskbarTheme,
        ILogger<TaskbarWidgetIndicator> log)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _taskbarTheme = taskbarTheme ?? throw new ArgumentNullException(nameof(taskbarTheme));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _viewModel = new TaskbarWidgetViewModel(_l);
        _window = new TaskbarWidgetWindow { DataContext = _viewModel };
        TaskbarInk.Apply(_window.Resources, _taskbarTheme.Current);

        // The hover card is a window of our own, placed from the widget's real
        // screen rectangle. Avalonia's ToolTip positions relative to the widget
        // window, and as a child of the taskbar that window's coordinates are not
        // something Avalonia models well: the card landed at the pointer, touching
        // the taskbar, and placement settings changed nothing.
        _hover = new TaskbarHoverWindow { DataContext = _viewModel };
        _hover.SizeChanged += (_, _) =>
        {
            _hoverSized = true;
            PlaceHover();
        };
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            ShowHover();
        };

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _noticeTimer.Tick += (_, _) => OnNoticeExpired();

        _window.ContextMenu = BuildMenu();
        _window.PointerReleased += OnPointerReleased;
        _window.PointerEntered += (_, _) => _hoverTimer.Start();
        _window.PointerExited += (_, _) => HideHover();
        _window.PointerPressed += (_, _) => HideHover();

        _l.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(RebuildMenu);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Reposition();
    }

    /// <inheritdoc />
    public event EventHandler? LeftClicked;

    /// <inheritdoc />
    public event EventHandler<ContextActionEventArgs>? MenuAction;

    /// <inheritdoc />
    public void Configure(IndicatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _viewModel.Configure(
            options.ThresholdPercent,
            options.ShowWeekFable,
            options.FollowSystem,
            StalePolicy.ThresholdFor(options.PollInterval));
    }

    /// <inheritdoc />
    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Parked off-screen first, the way the hover card is placed before it is
        // shown. Until Reposition has measured the taskbar there is nowhere for
        // this window to be: it is a top-level window at that point, not yet a
        // child of the taskbar, so Show would put a floating card wherever the
        // OS chose - over whatever the user was looking at - and the move into
        // the taskbar a moment later reads as a flash in the wrong place.
        //
        // Avalonia has already created the native window by now, so the position
        // takes effect without the window ever being seen at it.
        _window.Position = OffScreen;
        _window.Show();
        Reposition();
        _timer.Start();
    }

    /// <inheritdoc />
    public void Render(
        UsageSnapshot? snapshot, IndicatorMode mode, ThresholdState state, IndicatorAlert alert)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Render(snapshot, mode, state, alert));
            return;
        }

        _lastSnapshot = snapshot;
        _lastAlert = alert;

        // Re-read the taskbar's appearance on every render, as the icon does, so a
        // light/dark switch takes effect on the next poll. Cheap: three brushes.
        TaskbarInk.Apply(_window.Resources, _taskbarTheme.Current);
        _viewModel.Update(snapshot, alert, _clock.GetUtcNow());

        _currentMode = mode;
    }

    /// <summary>
    /// Re-measures the taskbar and moves the widget if its slot changed.
    /// </summary>
    /// <remarks>
    /// Runs every second. Cheap - a handful of <c>GetWindowRect</c> calls - and the
    /// move itself only happens when something actually moved, so the steady state
    /// costs nothing visible.
    /// </remarks>
    private void Reposition()
    {
        if (_disposed)
        {
            return;
        }

        nint handle = _window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0)
        {
            return;
        }

        if (!_host.IsAttached(handle))
        {
            // First time through, or Explorer restarted and took our parent with it.
            _lastSlot = null;
            if (!_host.Attach(handle))
            {
                if (!_warnedNoTaskbar)
                {
                    _warnedNoTaskbar = true;
                    // Off-screen is where Show parked it, and off-screen is where
                    // it stays until a retry succeeds. Better than the card it
                    // used to leave floating over the desktop: the timer tries
                    // again every second, and Explorer restarting is the usual
                    // reason to be here.
                    _log.LogWarning("The taskbar could not be joined. The widget is hidden until it can be.");
                }

                return;
            }

            _warnedNoTaskbar = false;
            _log.LogInformation("Widget attached to the taskbar.");
        }

        TaskbarMetrics? metrics = _host.Measure();
        if (metrics is null)
        {
            return;
        }

        _lastMetrics = metrics;

        // The window is sized in logical units and placed in physical ones. Its
        // height follows the taskbar; its width follows its content.
        double scale = _window.RenderScaling;
        double logicalHeight = metrics.Taskbar.Height / scale;
        if (Math.Abs(_window.Height - logicalHeight) > 0.5d)
        {
            _window.Height = logicalHeight;
        }

        int widthPx = (int)Math.Ceiling(_window.Bounds.Width * scale);
        TaskbarSlot? slot = TaskbarLayout.Compute(metrics, widthPx);
        if (slot is null)
        {
            if (!_warnedNoSlot)
            {
                _warnedNoSlot = true;
                _log.LogWarning(
                    "No room for the widget in a {Width}x{Height} taskbar.",
                    metrics.Taskbar.Width,
                    metrics.Taskbar.Height);
            }

            return;
        }

        _warnedNoSlot = false;
        if (slot != _lastSlot)
        {
            _host.Move(handle, slot);
            _lastSlot = slot;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            LeftClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void ShowNotice(string text, TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowNotice(text, duration));
            return;
        }

        // The hover card opens by itself with the warning at its foot, and goes
        // away again after the duration unless the pointer is on the widget - in
        // which case it is a hover, and hovers end when the pointer leaves.
        _viewModel.NoticeText = text ?? string.Empty;
        _noticeTimer.Stop();
        if (_viewModel.HasNotice)
        {
            ShowHover();
            _noticeTimer.Interval = duration <= TimeSpan.Zero ? TimeSpan.FromSeconds(8) : duration;
            _noticeTimer.Start();
        }
        else if (!_window.IsPointerOver)
        {
            HideHover();
        }
    }

    private void OnNoticeExpired()
    {
        _noticeTimer.Stop();
        if (!_window.IsPointerOver)
        {
            HideHover();
        }
    }

    private void ShowHover()
    {
        if (_disposed || !_viewModel.HasReading && string.IsNullOrEmpty(_viewModel.StatusText))
        {
            return;
        }

        if (!_hover.IsVisible)
        {
            // Placed before it is shown, from whatever size it had last time, so
            // it does not flash at a default position and then jump.
            PlaceHover();
            _hover.Show();
        }

        PlaceHover();
    }

    private void HideHover()
    {
        _hoverTimer.Stop();
        if (_noticeTimer.IsEnabled)
        {
            // A notice is still on its clock; the pointer leaving must not cut it short.
            return;
        }

        if (_hover.IsVisible)
        {
            _hover.Hide();
        }
    }

    /// <summary>
    /// Puts the hover card above the widget, right edges aligned, a gap apart.
    /// </summary>
    /// <remarks>
    /// Everything here is in physical pixels, straight from the taskbar host: the
    /// widget's slot within the taskbar plus the taskbar's own screen position is
    /// the widget's screen rectangle, and <see cref="Window.Position"/> takes
    /// physical pixels too. The gap is scaled by the card's own render scaling
    /// so it stays 10 layout units on any monitor.
    /// </remarks>
    private void PlaceHover()
    {
        if (_lastMetrics is null || _lastSlot is null)
        {
            return;
        }

        // Until the card has been laid out once, Bounds is the default window
        // size, not the card's; placing from that would flash a huge window at a
        // wrong spot. A typical size stands in, and SizeChanged corrects it.
        double scale = _hover.RenderScaling;
        double width = _hoverSized ? _hover.Bounds.Width : 340d;
        double height = _hoverSized ? _hover.Bounds.Height : 150d;
        int widthPx = (int)Math.Ceiling(width * scale);
        int heightPx = (int)Math.Ceiling(height * scale);

        int widgetRight = _lastMetrics.Taskbar.Left + _lastSlot.X + _lastSlot.Width;
        int widgetTop = _lastMetrics.Taskbar.Top + _lastSlot.Y;
        int gap = (int)Math.Round(HoverGap * scale);

        var position = new PixelPoint(widgetRight - widthPx, widgetTop - gap - heightPx);
        if (_hover.Position != position)
        {
            _hover.Position = position;
            _log.LogInformation(
                "Hover card placed at {Position}, {Width}x{Height} px, above widget right edge {Right}, top {Top}.",
                position,
                widthPx,
                heightPx,
                widgetRight,
                widgetTop);
        }
    }

    /// <summary>
    /// Builds the right-click menu: the tray icon's, minus its "Show ▸" submenu.
    /// </summary>
    /// <remarks>
    /// The submenu picks which one metric the icon draws. The widget draws them
    /// all, so the choice would change nothing visible here - the setting is kept
    /// and still applies the moment the user switches back to the icon.
    /// </remarks>
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateItem("Tray_Details", ContextAction.ShowDetails));
        menu.Items.Add(CreateItem("Tray_Report", ContextAction.ShowReport));
        menu.Items.Add(CreateItem("Tray_Refresh", ContextAction.Refresh));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("Tray_Config", ContextAction.OpenConfig));
        menu.Items.Add(CreateItem("Tray_Info", ContextAction.ShowInfo));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("Tray_Quit", ContextAction.Quit));
        return menu;
    }

    private void RebuildMenu()
    {
        if (_disposed)
        {
            return;
        }

        _window.ContextMenu = BuildMenu();
        Render(_lastSnapshot, _currentMode, ThresholdState.Unknown, _lastAlert);
    }

    private MenuItem CreateItem(string headerKey, ContextAction action) => new()
    {
        Header = _l[headerKey],
        Command = new RelayCommandShim(
            () => MenuAction?.Invoke(this, new ContextActionEventArgs(action))),
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _hoverTimer.Stop();
        _noticeTimer.Stop();
        _hover.Close();

        nint handle = _window.TryGetPlatformHandle()?.Handle ?? 0;
        _host.Detach(handle);
        _window.Close();
    }

    /// <summary>A minimal command for menu wiring; see <see cref="TrayIconIndicator"/>.</summary>
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
