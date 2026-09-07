using Avalonia.Media;
using ClaudeStatus.Theming;

namespace ClaudeStatus.App.ViewModels;

/// <summary>
/// One entry in the theme picker.
/// </summary>
/// <remarks>
/// Carries three swatch brushes so the dropdown can show what a theme looks like
/// without applying it. Picking a theme by name alone means trying all eleven to
/// find the one you want.
/// </remarks>
/// <param name="Id">Stored in settings. Empty or <c>system</c> means follow the OS.</param>
/// <param name="Name">Display name. A proper noun, so never translated.</param>
/// <param name="Theme">The theme itself, or null for the "follow the system" entry.</param>
public sealed record ThemeOption(string Id, string Name, Theme? Theme)
{
    /// <summary>Swatch for the background.</summary>
    public IBrush BackgroundSwatch => Brush(Theme?.Background);

    /// <summary>Swatch for the body text colour.</summary>
    public IBrush TextSwatch => Brush(Theme?.Text);

    /// <summary>Swatch for the primary colour.</summary>
    public IBrush PrimarySwatch => Brush(Theme?.Primary);

    /// <summary>Whether to draw swatches at all - the system entry has no fixed colours.</summary>
    public bool HasSwatches => Theme is not null;

    /// <inheritdoc />
    public override string ToString() => Name;

    private static IBrush Brush(Rgb? colour) => colour is { } value
        ? new SolidColorBrush(Color.FromRgb(value.R, value.G, value.B))
        : Brushes.Transparent;
}

/// <summary>
/// One entry in the font picker.
/// </summary>
/// <param name="Family">
/// The family or stack stored in settings. Empty means the platform default;
/// null marks the "Custom…" entry, which reads from the text box beside it.
/// </param>
/// <param name="Name">What the list shows.</param>
public sealed record FontOption(string? Family, string Name)
{
    /// <summary>True for the entry that defers to the typed family.</summary>
    public bool IsCustom => Family is null;

    /// <inheritdoc />
    public override string ToString() => Name;
}
