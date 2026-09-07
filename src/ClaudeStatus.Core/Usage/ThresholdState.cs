namespace ClaudeStatus.Usage;

/// <summary>Whether the displayed metric has crossed the user's alert threshold.</summary>
public enum ThresholdState
{
    /// <summary>Below the threshold. The icon renders normally.</summary>
    Normal = 0,

    /// <summary>At or above the threshold. The icon renders red.</summary>
    Exceeded = 1,

    /// <summary>The source gave us no value for this metric. The icon renders as unknown.</summary>
    Unknown = 2,
}
