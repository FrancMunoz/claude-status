using ClaudeStatus.Logging;

namespace ClaudeStatus.Theming;

/// <summary>
/// The fonts offered in the picker, and what counts as a usable family name.
/// </summary>
/// <remarks>
/// <para>
/// A curated list rather than an enumeration of everything installed. A typical
/// Windows machine has 300-odd families, and a dropdown of 300 names with no
/// preview is worse than useless - the shortlist is the ones that ship broadly
/// and stay readable at 11 px, which is the size most of this interface uses.
/// </para>
/// <para>
/// The list is a suggestion, not a constraint: the Config window also takes a
/// typed name, and Avalonia falls back to the default face when a family is not
/// installed. Each entry below is a comma-separated stack for that reason, so
/// "Segoe UI" on Windows and "SF Pro Text" on macOS resolve from one entry.
/// </para>
/// </remarks>
public static class FontCatalog
{
    /// <summary>The empty value, meaning "whatever the platform's default is".</summary>
    public const string SystemDefault = "";

    /// <summary>Longest family name we will store. Comfortably fits any real stack.</summary>
    public const int MaximumLength = 64;

    /// <summary>
    /// The suggested families, as font stacks.
    /// </summary>
    /// <remarks>
    /// Not translated: these are product names. The picker shows the OS default
    /// entry with a translated label and these verbatim.
    /// </remarks>
    public static IReadOnlyList<string> Suggested { get; } =
    [
        "Segoe UI, SF Pro Text, Ubuntu, Noto Sans",
        "Inter, Segoe UI, Helvetica Neue",
        "Cascadia Mono, Menlo, DejaVu Sans Mono",
        "Consolas, SF Mono, Liberation Mono",
        "Georgia, Iowan Old Style, Noto Serif",
        "Verdana, DejaVu Sans",
    ];

    /// <summary>
    /// Whether a family name is safe to persist and hand to the font system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The charset is what a font stack legitimately contains: letters, digits,
    /// spaces, and the handful of punctuation marks real family names use. It
    /// excludes everything that would make this a path, a URI or a script.
    /// </para>
    /// <para>
    /// The credential check is belt and braces. A 64-character cap already
    /// prevents a whole access token from fitting, but this field is the loosest
    /// in <c>settings.json</c> and the cost of being certain is one call. See
    /// <c>docs/security.md</c> §4.1.
    /// </para>
    /// </remarks>
    public static bool IsValidFamily(string? family)
    {
        if (string.IsNullOrEmpty(family))
        {
            // Empty is the valid "use the platform default" value.
            return true;
        }

        if (family.Length > MaximumLength)
        {
            return false;
        }

        foreach (char c in family)
        {
            bool legal = char.IsAsciiLetterOrDigit(c) || c is ' ' or ',' or '-' or '.' or '\'';
            if (!legal)
            {
                return false;
            }
        }

        return family.Trim().Length > 0 && Redactor.LooksRedacted(family);
    }

    /// <summary>Returns the family if it is usable, or the platform default if not.</summary>
    public static string Normalize(string? family)
        => IsValidFamily(family) ? (family ?? SystemDefault).Trim() : SystemDefault;
}
