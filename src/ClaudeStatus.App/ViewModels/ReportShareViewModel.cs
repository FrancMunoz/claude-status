using ClaudeStatus.Localization;
using ClaudeStatus.Usage;

namespace ClaudeStatus.App.ViewModels;

/// <summary>
/// One surface's share of the week, as a row in the report window.
/// </summary>
/// <remarks>Rebuilt rather than mutated, like <see cref="ReportRowViewModel"/>.</remarks>
public sealed class ReportShareViewModel
{
    public ReportShareViewModel(ILocalizer localizer, UsageBreakdownRow row)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(row);

        Label = Name(localizer, row);
        Percent = row.Percent;
        PercentText = localizer.Format("Bar_Percent", row.Percent);
    }

    /// <summary>The surface name.</summary>
    public string Label { get; }

    /// <summary>The share, 0-100, for the bar.</summary>
    public double Percent { get; }

    /// <summary>The share as text, e.g. "21 %".</summary>
    public string PercentText { get; }

    /// <summary>What to call a surface, in the current language when we know it.</summary>
    internal static string Name(ILocalizer localizer, UsageBreakdownRow row)
        => SurfaceKey(row.Key) is { } key
            ? localizer[key]

            // A surface added upstream has no translation yet. The source's own
            // label beats the raw key, and the raw key beats nothing.
            : row.DisplayName is { Length: > 0 } name ? name : row.Key;

    /// <summary>The resource key for a surface we know, or null.</summary>
    /// <remarks>Matched exactly: the keys are identifiers, not prose.</remarks>
    internal static string? SurfaceKey(string key) => key switch
    {
        "claude_code" => "Report_Surface_ClaudeCode",
        "chat" => "Report_Surface_Chat",
        "cowork" => "Report_Surface_Cowork",
        "other" => "Report_Surface_Other",
        _ => null,
    };
}
