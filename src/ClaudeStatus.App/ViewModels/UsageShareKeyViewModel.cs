namespace ClaudeStatus.App.ViewModels;

/// <summary>One entry in a split bar's legend: a swatch, a name and a share.</summary>
/// <param name="Label">The surface name, already translated.</param>
/// <param name="PercentText">Its share of the window's usage, formatted.</param>
/// <param name="Opacity">The swatch's strength, matching its segment in the bar.</param>
public sealed record UsageShareKeyViewModel(string Label, string PercentText, double Opacity);
