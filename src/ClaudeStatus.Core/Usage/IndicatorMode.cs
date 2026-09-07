namespace ClaudeStatus.Usage;

/// <summary>Which metric the tray indicator shows. Cycled from the context menu.</summary>
public enum IndicatorMode
{
    /// <summary>The rolling session window percentage. The default.</summary>
    SessionPercent = 0,

    /// <summary>The 7-day all-models percentage.</summary>
    WeekPercent = 1,

    /// <summary>The 7-day Fable-scoped percentage.</summary>
    WeekFablePercent = 2,

    /// <summary>A ring rendering of the session window rather than a number.</summary>
    Ring = 3,

    /// <summary>
    /// Every headline window at once, as a row of labelled percentages.
    /// </summary>
    /// <remarks>
    /// Only renderable where the indicator may be wider than it is tall - a macOS
    /// menu bar item, which grows to fit its image. A Windows notification-area
    /// icon is a fixed square, so anywhere
    /// <see cref="ClaudeStatus.Platform.IPlatformInfo.SupportsInlineTrayText"/> is
    /// false this falls back to <see cref="SessionPercent"/> rather than being
    /// squeezed into a box it cannot fit.
    /// </remarks>
    Row = 4,
}
