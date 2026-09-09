using Avalonia.Threading;
using ClaudeStatus.App.Branding;
using ClaudeStatus.Localization;
using ClaudeStatus.Platform;
using ClaudeStatus.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeStatus.App.Tray;

/// <summary>
/// Shows usage as text in a native status item, rather than as a rendered icon.
/// </summary>
/// <remarks>
/// <para>
/// The macOS menu bar path. Everything OS-specific is behind
/// <see cref="INativeStatusItem"/>; what is left here is the same job
/// <see cref="TrayIconIndicator"/> does - compose the reading, build the menu,
/// keep the radio tick honest - with the drawing handed to AppKit.
/// </para>
/// <para>
/// It buys the two things the icon could not have. The item sizes itself to its
/// text, so the row is as wide as it needs to be instead of clipped to a square;
/// and a left click is distinguishable from a right one, so the details popup
/// belongs to the left button and the menu to the right, which is what a menu bar
/// app is expected to do.
/// </para>
/// </remarks>
public sealed class NativeStatusIndicator : IStatusIndicator
{
    /// <summary>Offset for a plain command, clear of any tag a control sets itself.</summary>
    /// <remarks>
    /// The offsets exist so a tag that did not come from this menu decodes to
    /// nothing rather than to whichever command happens to sit at that number. An
    /// NSStatusBarButton, for one, reports its own tag as -1.
    /// </remarks>
    private const long ActionTagBase = 100;

    /// <summary>Offset for a metric choice, clear of the command range.</summary>
    private const long ModeTagBase = 200;

    /// <summary>
    /// The mark's size in physical pixels.
    /// </summary>
    /// <remarks>
    /// Twice the point size it is displayed at, so it is crisp on a HiDPI menu bar.
    /// Rendering at the display's own scale would mean asking the platform how big
    /// a pixel is before anything can be drawn, for one small fixed image.
    /// </remarks>
    private const int IconPixels = 32;

    private readonly INativeStatusItem _item;
    private readonly ILocalizer _l;
    private readonly ILogger<NativeStatusIndicator> _log;
    private readonly TimeProvider _clock;

    /// <summary>The last thing written, so only changes are reported.</summary>
    private (string Text, StatusTint Tint)? _shown;

    private IndicatorMode _currentMode = IndicatorMode.Row;
    private bool _showWeekFable;
    private TimeSpan _staleAfter = StalePolicy.Floor;
    private bool _disposed;

    /// <param name="item">The platform's status item.</param>
    /// <param name="localizer">Supplies the row labels and the menu.</param>
    /// <param name="log">Reports what reaches the menu bar, and in what colour.</param>
    /// <param name="clock">Judges how old a reading is. The system clock by default.</param>
    public NativeStatusIndicator(
        INativeStatusItem item,
        ILocalizer localizer,
        ILogger<NativeStatusIndicator>? log = null,
        TimeProvider? clock = null)
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _log = log ?? NullLogger<NativeStatusIndicator>.Instance;
        _clock = clock ?? TimeProvider.System;

        _item.LeftClicked += OnLeftClicked;
        _item.MenuItemClicked += OnMenuItemClicked;

        // Once. The mark never changes - its colour follows the text through the
        // platform's own tinting, so there is nothing here to keep in step.
        _item.SetIcon(AppMark.ToPng(IconPixels));

        RebuildMenu();

        // The menu is handed to the OS as text, so a language change has to rebuild
        // it. Once per language change, not per poll.
        _l.PropertyChanged += OnLanguageChanged;
    }

    /// <inheritdoc />
    public event EventHandler? LeftClicked;

    /// <inheritdoc />
    public event EventHandler<ContextActionEventArgs>? MenuAction;

    /// <inheritdoc />
    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _item.SetVisible(true);
    }

    /// <inheritdoc />
    public void Configure(IndicatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _showWeekFable = options.ShowWeekFable;

        _staleAfter = StalePolicy.ThresholdFor(options.PollInterval);
    }

    /// <inheritdoc />
    public void Render(
        UsageSnapshot? snapshot, IndicatorMode mode, ThresholdState state, IndicatorAlert alert)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Dispatcher.UIThread.CheckAccess())
        {
            // The poll loop runs on a worker thread, and AppKit is not thread-safe:
            // touching a status item off the main thread is undefined behaviour,
            // not a race that shows up occasionally.
            Dispatcher.UIThread.Post(() => Render(snapshot, mode, state, alert));
            return;
        }

        string text = Compose(snapshot, mode, alert);
        StatusTint tint = TintFor(snapshot, state, alert);

        // Only on a change. The colour is the thing people ask about - "why is it
        // grey" has one answer and it is this line - and logging it every poll
        // would bury that answer under a copy of itself once a minute.
        if (_shown != (text, tint))
        {
            _shown = (text, tint);
            _log.LogInformation(
                "Menu bar shows \"{Text}\" as {Tint}. stale={Stale} state={State} alert={Alert}",
                text,
                tint,
                snapshot?.IsStale,
                state,
                alert);
        }

        _item.SetTitle(text, tint);

        if (_currentMode != mode)
        {
            _currentMode = mode;
            RebuildMenu();
        }
    }

    /// <summary>Builds the text for a mode.</summary>
    /// <remarks>
    /// Only <see cref="IndicatorMode.Row"/> has several readings to show. The
    /// single-metric modes stay available here - someone who wants one number in
    /// their menu bar should be able to have it - and the ring does not, because a
    /// text item has no way to draw one; it falls back to the session number.
    /// </remarks>
    private string Compose(UsageSnapshot? snapshot, IndicatorMode mode, IndicatorAlert alert)
    {
        if (mode == IndicatorMode.Row)
        {
            return IndicatorText.ComposeRow(
                snapshot,
                alert,
                (_l["Widget_Session"], _l["Widget_Week"], _l["Tray_Row_Fable"]),
                _showWeekFable,
                now: _clock.GetUtcNow());
        }

        (string Label, UsageWindow? Window) single = mode switch
        {
            IndicatorMode.WeekPercent => (_l["Widget_Week"], snapshot?.Week),
            IndicatorMode.WeekFablePercent => (_l["Tray_Row_Fable"], snapshot?.WeekFable),
            _ => (_l["Widget_Session"], snapshot?.Session),
        };

        // The same countdown as the row, so switching to one metric does not lose
        // it. Only the session window is short enough for one; FormatCountdown
        // returns nothing for the others, which is why this needs no mode check.
        string countdown = alert == IndicatorAlert.None
            ? IndicatorText.FormatCountdown(single.Window?.TimeUntilReset(_clock.GetUtcNow()))
            : string.Empty;

        return $"{single.Label} {countdown}{(countdown.Length > 0 ? " " : string.Empty)}"
            + IndicatorText.WindowValue(single.Window, alert, withSign: true);
    }

    /// <summary>Picks the colour, or leaves it to the menu bar.</summary>
    /// <remarks>
    /// Staleness loses to an alert on purpose. A stale reading that is over the
    /// threshold is still the best evidence available that the user is near their
    /// limit, and dimming it would be the one case where the warning is softened
    /// exactly when it matters.
    /// </remarks>
    private StatusTint TintFor(
        UsageSnapshot? snapshot, ThresholdState state, IndicatorAlert alert)
    {
        if (state == ThresholdState.Exceeded || alert == IndicatorAlert.NeedsCredential)
        {
            return StatusTint.Alert;
        }

        return StalePolicy.ShowsAsStale(snapshot, _clock.GetUtcNow(), _staleAfter)
            ? StatusTint.Stale
            : StatusTint.Normal;
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => Dispatcher.UIThread.Post(RebuildMenu);

    /// <summary>Builds the menu described in <c>docs/manual.md</c> §3.</summary>
    /// <remarks>
    /// Details stays first even though a left click now reaches it directly. The
    /// menu is still the only route for anyone using the keyboard or a trackpad
    /// configured without a secondary button.
    /// </remarks>
    private void RebuildMenu()
    {
        if (_disposed)
        {
            return;
        }

        var modes = new List<StatusMenuEntry>
        {
            ModeEntry("Tray_Mode_Row", IndicatorMode.Row),
            ModeEntry("Tray_Mode_Session", IndicatorMode.SessionPercent),
            ModeEntry("Tray_Mode_Week", IndicatorMode.WeekPercent),
            ModeEntry("Tray_Mode_WeekFable", IndicatorMode.WeekFablePercent),
        };

        _item.SetMenu(
        [
            ActionEntry("Tray_Details", ContextAction.ShowDetails),
            ActionEntry("Tray_Report", ContextAction.ShowReport),
            new StatusMenuEntry(_l["Tray_Show"], Submenu: modes),
            ActionEntry("Tray_Refresh", ContextAction.Refresh),
            StatusMenuEntry.Separator,
            ActionEntry("Tray_Config", ContextAction.OpenConfig),
            ActionEntry("Tray_Info", ContextAction.ShowInfo),
            StatusMenuEntry.Separator,
            ActionEntry("Tray_Quit", ContextAction.Quit),
        ]);
    }

    private StatusMenuEntry ActionEntry(string headerKey, ContextAction action)
        => new(_l[headerKey], ActionTagBase + (long)action);

    private StatusMenuEntry ModeEntry(string headerKey, IndicatorMode mode)
        => new(_l[headerKey], ModeTagBase + (long)mode, IsChecked: _currentMode == mode);

    private void OnLeftClicked(object? sender, EventArgs e)
        => LeftClicked?.Invoke(this, EventArgs.Empty);

    /// <summary>Turns a menu tag back into the action it stands for.</summary>
    private void OnMenuItemClicked(object? sender, long tag)
    {
        if (tag >= ModeTagBase)
        {
            var mode = (IndicatorMode)(tag - ModeTagBase);
            if (Enum.IsDefined(mode))
            {
                MenuAction?.Invoke(
                    this, new ContextActionEventArgs(ContextAction.ChangeMode, mode));
            }

            return;
        }

        var action = (ContextAction)(tag - ActionTagBase);
        if (Enum.IsDefined(action))
        {
            MenuAction?.Invoke(this, new ContextActionEventArgs(action));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _l.PropertyChanged -= OnLanguageChanged;
        _item.LeftClicked -= OnLeftClicked;
        _item.MenuItemClicked -= OnMenuItemClicked;
        _item.Dispose();
    }
}
