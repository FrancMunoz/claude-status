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
    /// The longest countdown worth showing as a clock.
    /// </summary>
    /// <remarks>
    /// A five-hour window counts down in hours and minutes, which is what
    /// <c>h:mm</c> says. A seven-day one would read <c>(139:12)</c>, a number
    /// nobody converts back into "Thursday", so the weekly windows get none.
    /// </remarks>
    public static readonly TimeSpan CountdownCeiling = TimeSpan.FromDays(1);

    /// <summary>
    /// How long is left of a window, as a bracketed clock: <c>(2:11)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brackets are part of the token, not decoration a caller adds: this sits
    /// between a label and a percentage - <c>5h (2:11) 56%</c> - and without them
    /// the row is three numbers in a line, one of which is not a percentage.
    /// </para>
    /// <para>
    /// Empty for anything <see cref="CountdownCeiling"/> or longer, and for a
    /// window the source gave no reset time for. Invariant digits, like
    /// <see cref="FormatPercent"/>: this is a readout, not prose, and the widget
    /// and the macOS menu bar must not disagree about its shape.
    /// </para>
    /// </remarks>
    public static string FormatCountdown(TimeSpan? remaining)
    {
        if (remaining is not { } left || left >= CountdownCeiling)
        {
            return string.Empty;
        }

        if (left < TimeSpan.Zero)
        {
            left = TimeSpan.Zero;
        }

        return string.Create(
            CultureInfo.InvariantCulture, $"({(int)left.TotalHours}:{left.Minutes:00})");
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
    /// <param name="now">
    /// The current time, which adds the session countdown - <c>5h (2:11) 56%</c>.
    /// Omitted, the row is labels and percentages as before. Only the session
    /// carries one; see <see cref="FormatCountdown"/> for why the weekly ones do
    /// not, and note that an alert or a missing window replaces the percentage
    /// with a symbol, at which point a countdown beside it would be dressing up
    /// the absence of a reading as one.
    /// </param>
    public static string ComposeRow(
        UsageSnapshot? snapshot,
        IndicatorAlert alert,
        (string Session, string Week, string WeekFable) labels,
        bool showWeekFable,
        string separator = " · ",
        DateTimeOffset? now = null)
    {
        string countdown = now is { } at && alert == IndicatorAlert.None && snapshot?.Session is { } session
            ? FormatCountdown(session.TimeUntilReset(at))
            : string.Empty;

        var parts = new List<string>(3)
        {
            $"{labels.Session} {countdown}{(countdown.Length > 0 ? " " : string.Empty)}"
                + WindowValue(snapshot?.Session, alert, withSign: true),
            $"{labels.Week} {WindowValue(snapshot?.Week, alert, withSign: true)}",
        };

        if (showWeekFable && snapshot?.WeekFable is not null)
        {
            parts.Add($"{labels.WeekFable} {WindowValue(snapshot.WeekFable, alert, withSign: true)}");
        }

        return string.Join(separator, parts);
    }
}
