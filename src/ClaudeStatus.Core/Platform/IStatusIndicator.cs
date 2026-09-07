using ClaudeStatus.Theming;
using ClaudeStatus.Usage;

namespace ClaudeStatus.Platform;

/// <summary>What the user picked from the tray context menu.</summary>
public enum ContextAction
{
    /// <summary>Open the details window.</summary>
    ShowDetails = 0,

    /// <summary>Force a refresh now.</summary>
    Refresh = 1,

    /// <summary>Open the configuration window.</summary>
    OpenConfig = 2,

    /// <summary>Open the about window.</summary>
    ShowInfo = 3,

    /// <summary>Quit the application.</summary>
    Quit = 4,

    /// <summary>Change which metric the icon shows. Carries the new mode.</summary>
    ChangeMode = 5,

    /// <summary>Open the full report window.</summary>
    ShowReport = 6,
}

/// <summary>A menu action, with the mode when the action is <see cref="ContextAction.ChangeMode"/>.</summary>
public sealed class ContextActionEventArgs(ContextAction action, IndicatorMode? mode = null) : EventArgs
{
    /// <summary>What was chosen.</summary>
    public ContextAction Action { get; } = action;

    /// <summary>The requested mode, when <see cref="Action"/> is <see cref="ContextAction.ChangeMode"/>.</summary>
    public IndicatorMode? Mode { get; } = mode;
}

/// <summary>Something the user must act on, overriding the usual reading.</summary>
public enum IndicatorAlert
{
    /// <summary>Nothing wrong. Show the usage as normal.</summary>
    None = 0,

    /// <summary>
    /// There is no usable credential, so the numbers can never arrive.
    /// </summary>
    /// <remarks>
    /// Distinct from a network failure because the user has to do something -
    /// sign in to Claude Code, or set a token. A left click goes straight to
    /// Config rather than to a details window with three dashes in it.
    /// </remarks>
    NeedsCredential = 1,

    /// <summary>
    /// The endpoint cannot be reached, but the credential is probably fine.
    /// </summary>
    /// <remarks>
    /// Nothing for the user to do, so this only softens the icon and explains
    /// itself in the tooltip. It does not hijack the click.
    /// </remarks>
    Unreachable = 2,
}

/// <summary>The per-metric settings a multi-metric indicator needs.</summary>
/// <param name="ThresholdPercent">Where a metric turns red.</param>
/// <param name="ShowWeekFable">Whether the weekly Fable limit gets a column of its own.</param>
/// <param name="FollowSystem">
/// Whether the indicator blends into the taskbar - transparent, in the taskbar's
/// own text colour - rather than drawing the theme's card.
/// </param>
/// <param name="PollInterval">
/// How often a fresh reading is expected, when the caller knows.
/// </param>
/// <remarks>
/// <see cref="PollInterval"/> is what lets an indicator judge whether a reading is
/// genuinely out of date rather than merely one failed request behind. Optional
/// because most indicators do not care: only the macOS menu bar uses it, and it
/// falls back to a fixed floor without it.
/// </remarks>
public sealed record IndicatorOptions(
    double ThresholdPercent,
    bool ShowWeekFable,
    bool FollowSystem = false,
    TimeSpan? PollInterval = null);

/// <summary>
/// How usage is shown in the tray or menu bar.
/// </summary>
/// <remarks>
/// The first implementation renders a bitmap into an Avalonia <c>TrayIcon</c>.
/// Deliberately an interface so a native <c>NSStatusItem</c> with inline text, or
/// a Windows widget, can be dropped in later without touching anything else
/// (<c>PLAN.md</c> Phase 8).
/// </remarks>
public interface IStatusIndicator : IDisposable
{
    /// <summary>Draws the current state.</summary>
    /// <param name="snapshot">The reading to show. Null before the first fetch.</param>
    /// <param name="mode">Which metric to display.</param>
    /// <param name="state">Whether the displayed metric has crossed the threshold.</param>
    /// <param name="alert">Anything the user needs to act on.</param>
    void Render(UsageSnapshot? snapshot, IndicatorMode mode, ThresholdState state, IndicatorAlert alert);

    /// <summary>
    /// Applies the settings an indicator that shows more than one metric needs.
    /// </summary>
    /// <remarks>
    /// <see cref="Render"/> carries the threshold verdict for the one metric in
    /// <see cref="IndicatorMode"/>, which is all the tray icon needs. The taskbar
    /// widget shows up to three, judges the others itself, and can hide one. A
    /// default no-op so the icon is not obliged to care.
    /// </remarks>
    void Configure(IndicatorOptions options)
    {
    }

    /// <summary>
    /// Puts a short warning in front of the user without taking their focus.
    /// </summary>
    /// <param name="text">The sentence to show, already localised by the caller.</param>
    /// <param name="duration">How long to keep it up before it goes away by itself.</param>
    /// <remarks>
    /// Used for the velocity alert. A default no-op: the tray icon has no surface
    /// of its own to say it on, and the warning still reaches the user as a banner
    /// in the details popup the next time they open it.
    /// </remarks>
    void ShowNotice(string text, TimeSpan duration)
    {
    }

    /// <summary>Makes the indicator visible.</summary>
    void Show();

    /// <summary>Raised when the user activates the indicator itself (a left click).</summary>
    event EventHandler? LeftClicked;

    /// <summary>Raised when the user picks something from the context menu.</summary>
    event EventHandler<ContextActionEventArgs>? MenuAction;
}
