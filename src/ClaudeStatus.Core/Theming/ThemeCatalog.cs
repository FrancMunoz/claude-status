namespace ClaudeStatus.Theming;

/// <summary>
/// The themes that ship with the app, and how one gets chosen.
/// </summary>
/// <remarks>
/// <para>
/// Pure data and pure functions, like <see cref="Localization.LanguageCatalog"/>.
/// </para>
/// <para>
/// The palettes are built around a saturated primary against a low-chroma
/// background of the same hue family, which is what keeps them looking composed
/// rather than merely colourful. Every one is asserted in the tests to clear WCAG
/// 4.5:1 on body text and 3:1 on the muted grey - a theme that cannot be read is
/// not a theme, however good the swatch looks.
/// </para>
/// </remarks>
public static class ThemeCatalog
{
    /// <summary>The id meaning "follow the operating system's light or dark setting".</summary>
    public const string SystemId = "system";

    /// <summary>The theme used when the OS is in light mode, and the fallback for anything unknown.</summary>
    public static Theme Light { get; } = new(
        Id: "light",
        Name: "Light",
        Primary: new Rgb(0x1A, 0x6F, 0xE8),
        Background: new Rgb(0xFC, 0xFC, 0xFD),
        Text: new Rgb(0x1A, 0x1C, 0x20),
        Alert: new Rgb(0xC4, 0x18, 0x1B));

    /// <summary>The theme used when the OS is in dark mode.</summary>
    public static Theme Dark { get; } = new(
        Id: "dark",
        Name: "Dark",
        Primary: new Rgb(0x4C, 0x8D, 0xFF),
        Background: new Rgb(0x16, 0x18, 0x1D),
        Text: new Rgb(0xE8, 0xEA, 0xED),
        Alert: new Rgb(0xFF, 0x5D, 0x5F));

    /// <summary>
    /// Every theme that ships, in the order the picker lists them.
    /// </summary>
    /// <remarks>
    /// Light and Dark first because they are what most people want, then the rest
    /// roughly warm to cool. The names are proper nouns and are never translated:
    /// a user telling someone else "I'm using Nebula" should be understood.
    /// </remarks>
    public static IReadOnlyList<Theme> BuiltIn { get; } =
    [
        Light,
        Dark,

        // Anthropic's own palette: the ivory paper and the coral accent.
        new Theme(
            Id: "claude",
            Name: "Claude",
            Primary: new Rgb(0xC9, 0x5F, 0x3A),
            Background: new Rgb(0xF7, 0xF4, 0xEE),
            Text: new Rgb(0x24, 0x21, 0x1D),
            Alert: new Rgb(0xB4, 0x2A, 0x25)),

        // Warm dark, orange on near-black brown.
        new Theme(
            Id: "ember",
            Name: "Ember",
            Primary: new Rgb(0xFF, 0x8A, 0x3D),
            Background: new Rgb(0x1A, 0x12, 0x10),
            Text: new Rgb(0xFF, 0xED, 0xE4),
            Alert: new Rgb(0xFF, 0x4D, 0x4D)),

        // Deep blue with a bright azure primary.
        new Theme(
            Id: "ocean",
            Name: "Ocean",
            Primary: new Rgb(0x38, 0x9B, 0xFF),
            Background: new Rgb(0x0B, 0x14, 0x24),
            Text: new Rgb(0xDC, 0xE9, 0xFF),
            Alert: new Rgb(0xFF, 0x5C, 0x7A)),

        // Teal-green on near-black, the brightest primary of the set.
        new Theme(
            Id: "aurora",
            Name: "Aurora",
            Primary: new Rgb(0x21, 0xE0, 0xB8),
            Background: new Rgb(0x0D, 0x1A, 0x19),
            Text: new Rgb(0xDF, 0xF5, 0xF0),
            Alert: new Rgb(0xFF, 0x5E, 0x8A)),

        // Violet on aubergine.
        new Theme(
            Id: "nebula",
            Name: "Nebula",
            Primary: new Rgb(0xB4, 0x84, 0xFF),
            Background: new Rgb(0x15, 0x11, 0x21),
            Text: new Rgb(0xED, 0xE7, 0xFA),
            Alert: new Rgb(0xFF, 0x5C, 0x9B)),

        // Magenta-pink on charcoal, the most saturated dark theme.
        new Theme(
            Id: "orchid",
            Name: "Orchid",
            Primary: new Rgb(0xFF, 0x6E, 0xC7),
            Background: new Rgb(0x1A, 0x12, 0x1C),
            Text: new Rgb(0xFA, 0xE7, 0xF4),
            Alert: new Rgb(0xFF, 0x5A, 0x5A)),

        // Light and pink, the counterpart to Orchid. The alert is pulled toward
        // orange-red rather than the crimson that would sit naturally beside this
        // primary: two pinks a few degrees apart cannot be told apart at a glance,
        // which is the one thing an alert colour must never be.
        new Theme(
            Id: "sakura",
            Name: "Sakura",
            Primary: new Rgb(0xD6, 0x2B, 0x74),
            Background: new Rgb(0xFF, 0xF7, 0xFA),
            Text: new Rgb(0x2A, 0x1E, 0x24),
            Alert: new Rgb(0xB3, 0x26, 0x1E)),

        // Light and warm, amber on cream.
        new Theme(
            Id: "solar",
            Name: "Solar",
            Primary: new Rgb(0xC2, 0x74, 0x00),
            Background: new Rgb(0xFF, 0xFB, 0xF0),
            Text: new Rgb(0x26, 0x1F, 0x12),
            Alert: new Rgb(0xC4, 0x38, 0x18)),

        // Light and green, the calmest of the light set.
        new Theme(
            Id: "matcha",
            Name: "Matcha",
            Primary: new Rgb(0x2F, 0x8C, 0x4A),
            Background: new Rgb(0xF7, 0xFB, 0xF4),
            Text: new Rgb(0x1B, 0x24, 0x1A),
            Alert: new Rgb(0xC0, 0x33, 0x2B)),
    ];

    /// <summary>Whether an id is shaped like one we would accept.</summary>
    /// <remarks>
    /// Bounded for the same reason as the language tag: this ends up in
    /// <c>settings.json</c> and in a file path, so it must be short, lower-case
    /// and free of separators. See <c>docs/security.md</c> §4.1.
    /// </remarks>
    public static bool IsValidId(string? id)
        => !string.IsNullOrEmpty(id)
        && id.Length <= 32
        && char.IsAsciiLetterLower(id[0])
        && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>
    /// Picks the theme to use.
    /// </summary>
    /// <param name="id">The user's choice, or <see cref="SystemId"/> / empty to follow the OS.</param>
    /// <param name="available">Every theme on offer, built-in plus any loaded from files.</param>
    /// <param name="systemIsDark">Whether the OS is currently in dark mode.</param>
    /// <remarks>
    /// A chosen theme that no longer exists - the user deleted its file - falls
    /// back to following the system rather than to a hardcoded default, so the app
    /// still looks right rather than merely looking consistent.
    /// </remarks>
    public static Theme Resolve(string? id, IEnumerable<Theme> available, bool systemIsDark)
    {
        ArgumentNullException.ThrowIfNull(available);

        if (!string.IsNullOrEmpty(id)
            && !string.Equals(id, SystemId, StringComparison.OrdinalIgnoreCase))
        {
            foreach (Theme theme in available)
            {
                if (string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return theme;
                }
            }
        }

        return systemIsDark ? Dark : Light;
    }
}
