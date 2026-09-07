using System.Globalization;
using ClaudeStatus.Platform;

namespace ClaudeStatus.Usage;

/// <summary>
/// What the indicator says, independent of how it is drawn.
/// </summary>
/// <remarks>
/// <para>
/// Two indicators now write the same readings: the rendered bitmap that a Windows
/// tray needs, and the macOS menu bar item, whose text the OS draws itself. The
/// vocabulary has to be identical between them - someone moving between machines
/// should not have to learn that <c>x</c> means one thing here and another there -
/// so it is defined once, here, rather than in each renderer.
/// </para>
/// <para>
/// Formatting only. Nothing in this file knows about fonts, colours or pixels.
/// </para>
/// </remarks>
public static class IndicatorText
{
    /// <summary>
    /// Opacity of a reading that is no longer current.
    /// </summary>
    /// <remarks>
    /// Chosen to be unmistakable beside a fresh reading while still perfectly
    /// legible on its own - the number is old, not unavailable, and it is still the
    /// best information there is. Shared so the Windows icon and the macOS menu bar
    /// fade by the same amount rather than drifting apart.
    /// </remarks>
    public const double StaleAlpha = 0.55d;

    /// <summary>
    /// The reading at which a window counts as spent.
    /// </summary>
    /// <remarks>
    /// The same rounding <see cref="FormatPercent"/> uses, so an indicator can
    /// never show a cross while the tooltip beside it still says 99 %.
    /// </remarks>
    public static bool IsExhausted(double percent) => percent >= 99.95d;

    /// <summary>
    /// Formats a percentage as a whole number.
    /// </summary>
    /// <remarks>
    /// Rounding is away from zero, so 99.6 shows as 100 rather than suggesting
    /// there is headroom left.
    /// </remarks>
    public static string FormatPercent(double percent)
    {
        int rounded = (int)Math.Round(Math.Clamp(percent, 0d, 100d), MidpointRounding.AwayFromZero);
        return rounded.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What one window contributes to an indicator, as a short token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three of the four answers are shapes rather than numbers, and each replaces
    /// a number that would have been misleading:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>!</c> - no credential. Nothing useful can arrive until the user acts,
    ///     and a dash there looks like a glitch that might clear itself.
    ///   </description></item>
    ///   <item><description>
    ///     <c>x</c> - spent. "100" and "99" differ by one glyph and mean quite
    ///     different things: one is nearly out, the other is out.
    ///   </description></item>
    ///   <item><description>
    ///     <c>--</c> - no reading. Distinct from zero, which is a real answer.
    ///   </description></item>
    /// </list>
    /// </remarks>
    /// <param name="window">The window, or null when the source said nothing about it.</param>
    /// <param name="alert">Anything overriding the reading.</param>
    /// <param name="withSign">Whether a percentage carries its <c>%</c>.</param>
    public static string WindowValue(UsageWindow? window, IndicatorAlert alert, bool withSign)
    {
        if (alert == IndicatorAlert.NeedsCredential)
        {
            return "!";
        }

        if (window is null)
        {
            return "--";
        }

        if (IsExhausted(window.Percent))
        {
            return "x";
        }

        return withSign ? FormatPercent(window.Percent) + "%" : FormatPercent(window.Percent);
    }

    /// <summary>
    /// Composes the whole row: every headline window as a labelled percentage.
    /// </summary>
    /// <remarks>
    /// The Fable pair is dropped both when the user did not ask for it and when the
    /// account has no such limit - a label with a dash beside it reads as a fault
    /// rather than as "your plan does not have this one".
    /// </remarks>
    /// <param name="labels">The short labels, in session, week, Fable order.</param>
    /// <param name="separator">What goes between pairs.</param>
    public static string ComposeRow(
        UsageSnapshot? snapshot,
        IndicatorAlert alert,
        (string Session, string Week, string WeekFable) labels,
        bool showWeekFable,
        string separator = " · ")
    {
        var parts = new List<string>(3)
        {
            $"{labels.Session} {WindowValue(snapshot?.Session, alert, withSign: true)}",
            $"{labels.Week} {WindowValue(snapshot?.Week, alert, withSign: true)}",
        };

        if (showWeekFable && snapshot?.WeekFable is not null)
        {
            parts.Add($"{labels.WeekFable} {WindowValue(snapshot.WeekFable, alert, withSign: true)}");
        }

        return string.Join(separator, parts);
    }
}
