using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using ClaudeStatus.App.ViewModels;
using ClaudeStatus.App.Views;

namespace ClaudeStatus.App.Tray;

/// <summary>
/// The surface an icon does not have: a small card near the tray that can carry
/// a sentence for a few seconds and then take itself away.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TaskbarWidgetIndicator"/> can show a velocity warning at the foot
/// of its own hover card, because it is a window and has somewhere to put one. A
/// tray icon and a macOS menu-bar item have nowhere - which meant that on Linux,
/// on macOS, and on any Windows machine falling back to the icon, the warning
/// only ever reached the user if they happened to open the details popup
/// afterwards. This is the same card, shown by itself, so
/// <see cref="ClaudeStatus.Platform.IStatusIndicator.ShowNotice"/> means something
/// on every platform.
/// </para>
/// <para>
/// Placed with <see cref="TrayPopupPlacement"/>, the same geometry the details
/// popup uses, so the card appears by the tray rather than in the middle of
/// whatever the user was reading. It never takes focus: the window is declared
/// non-activating and unfocusable, and the point of a notice is that it does not
/// interrupt.
/// </para>
/// </remarks>
internal sealed class UsageNoticeCard : IDisposable
{
    /// <summary>How long a notice stays up when the caller does not say.</summary>
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(8);

    private readonly TaskbarWidgetViewModel _viewModel;
    private readonly bool _anchorAtTop;
    private readonly DispatcherTimer _timer;
    private UsageCardWindow? _window;
    private bool _disposed;

    /// <param name="viewModel">
    /// The card's contents - the same view model the caller feeds from
    /// <c>Render</c>, so the card shows the current numbers under the warning
    /// rather than a snapshot from whenever it was last opened.
    /// </param>
    /// <param name="anchorAtTop">
    /// True where the tray runs along the top edge, from
    /// <see cref="ClaudeStatus.Platform.IPlatformInfo.TrayIsAtTop"/>. The placement
    /// helper infers the edge from the screen insets otherwise, and on macOS
    /// infers it wrongly - the Dock is deeper than the menu bar.
    /// </param>
    public UsageNoticeCard(TaskbarWidgetViewModel viewModel, bool anchorAtTop)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _anchorAtTop = anchorAtTop;

        _timer = new DispatcherTimer { Interval = DefaultDuration };
        _timer.Tick += (_, _) => Hide();
    }

    /// <summary>Whether the card is on screen.</summary>
    public bool IsShowing => _window?.IsVisible == true;

    /// <summary>
    /// Puts <paramref name="text"/> on screen for <paramref name="duration"/>.
    /// </summary>
    /// <remarks>
    /// Empty text takes the card down instead: that is how the caller says the
    /// pace has come back to normal, and a card holding an empty warning panel
    /// would be a rectangle with nothing in it.
    /// </remarks>
    public void Show(string? text, TimeSpan duration)
    {
        if (_disposed)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(text, duration));
            return;
        }

        _viewModel.NoticeText = text ?? string.Empty;
        _timer.Stop();

        if (!_viewModel.HasNotice)
        {
            Hide();
            return;
        }

        UsageCardWindow window = _window ??= Create();

        // Placed before it is shown, from whatever size it had last time, so it
        // does not flash at a default position and then jump. The SizeChanged
        // hook corrects it once there is a real size to place from.
        Place(window);
        if (!window.IsVisible)
        {
            window.Show();
        }

        Place(window);

        _timer.Interval = duration > TimeSpan.Zero ? duration : DefaultDuration;
        _timer.Start();
    }

    /// <summary>Takes the card down and clears the warning it was carrying.</summary>
    public void Hide()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Hide);
            return;
        }

        _timer.Stop();
        _viewModel.NoticeText = string.Empty;
        _window?.Hide();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _window?.Close();
        _window = null;
    }

    private UsageCardWindow Create()
    {
        var window = new UsageCardWindow { DataContext = _viewModel };

        // The card sizes itself to its content, and the content is not known
        // until it has been laid out - which happens after the first Show. Until
        // then the placement is an estimate; this corrects it.
        window.SizeChanged += (_, _) => Place(window);
        return window;
    }

    /// <summary>Puts the card near the tray, fully on screen.</summary>
    /// <remarks>
    /// Recomputed on every notice rather than once, so moving the taskbar,
    /// changing the resolution or unplugging a monitor cannot leave the card
    /// stranded where the screen used to be.
    /// </remarks>
    private void Place(UsageCardWindow window)
    {
        IReadOnlyList<Screen> all = window.Screens.All;
        Screen? screen = window.Screens.Primary ?? (all.Count > 0 ? all[0] : null);
        if (screen is null)
        {
            return;
        }

        Size laidOut = window.Bounds.Size;
        var size = new Size(
            laidOut.Width > 0 ? laidOut.Width : window.Width,
            laidOut.Height > 0 ? laidOut.Height : window.Height);

        window.Position = TrayPopupPlacement.Place(
            screen.Bounds,
            screen.WorkingArea,
            size,
            screen.Scaling,
            _anchorAtTop);
    }
}
