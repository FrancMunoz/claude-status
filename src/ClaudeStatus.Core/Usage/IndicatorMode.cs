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
}
