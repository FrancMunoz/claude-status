using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeStatus.Localization;

/// <summary>
/// The languages the app knows about, and how one gets chosen.
/// </summary>
/// <remarks>
/// Pure data and pure functions - no state, per <c>docs/manual.md</c> §6.
/// </remarks>
public static partial class LanguageCatalog
{
    /// <summary>
    /// Languages compiled into the app, as BCP-47 tags.
    /// </summary>
    /// <remarks>
    /// English is first because it is the neutral resource set: every other
    /// language falls back to it key by key, so a half-finished translation shows
    /// English for the missing lines rather than blanks.
    /// </remarks>
    public static readonly IReadOnlyList<string> BuiltIn = ["en", "es", "ca", "de", "fr"];

    /// <summary>The tag meaning "whatever the operating system is set to".</summary>
    public const string FollowSystem = "";

    /// <summary>
    /// Longest tag we will accept.
    /// </summary>
    /// <remarks>
    /// This is a security bound, not a style choice. <c>AppSettings.LanguageTag</c>
    /// is the only free-text field the settings file has, and the length and
    /// character rules together make it impossible for a credential to be
    /// smuggled into it (<c>docs/security.md</c> §4).
    /// </remarks>
    public const int MaximumTagLength = 12;

    [GeneratedRegex(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern { get; }

    /// <summary>Whether a tag is shaped like a language tag we would accept.</summary>
    public static bool IsValidTag(string? tag)
        => !string.IsNullOrEmpty(tag)
        && tag.Length <= MaximumTagLength
        && TagPattern.IsMatch(tag);

    /// <summary>
    /// Picks the culture to run in.
    /// </summary>
    /// <param name="settingTag">The user's choice, or empty to follow the system.</param>
    /// <param name="available">Every tag that has translations, built-in or added.</param>
    /// <param name="systemCulture">The operating system's UI culture.</param>
    /// <remarks>
    /// A system culture of <c>es-AR</c> matches a translation of <c>es</c>, because
    /// the parent chain is walked before giving up. Anything with no match at all
    /// lands on English, which always exists.
    /// </remarks>
    public static CultureInfo Resolve(
        string? settingTag, IEnumerable<string> available, CultureInfo systemCulture)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(systemCulture);

        var tags = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);

        // An explicit choice wins outright, even over a better system match. If the
        // user asked for Catalan on a German machine, they meant it.
        if (IsValidTag(settingTag) && tags.Contains(settingTag!))
        {
            return CultureInfo.GetCultureInfo(settingTag!);
        }

        for (CultureInfo culture = systemCulture;
             !string.IsNullOrEmpty(culture.Name);
             culture = culture.Parent)
        {
            if (tags.Contains(culture.Name))
            {
                return culture;
            }
        }

        return CultureInfo.GetCultureInfo("en");
    }

    /// <summary>
    /// The name of a language, written in that language.
    /// </summary>
    /// <remarks>
    /// Taken from the OS culture data rather than the resource files, so a language
    /// someone adds by dropping in a JSON file gets a correct name for free.
    /// <see cref="CultureInfo.NativeName"/> is lower-case in several languages
    /// (français, español), which looks like a typo in a list, so the first letter
    /// is capitalised using that language's own casing rules.
    /// </remarks>
    public static string NativeName(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        string name = culture.NativeName;
        if (name.Length == 0)
        {
            return culture.Name;
        }

        // A region-qualified NativeName reads "español (España)"; the parenthetical
        // is noise in a language picker.
        int parenthesis = name.IndexOf(" (", StringComparison.Ordinal);
        if (parenthesis > 0)
        {
            name = name[..parenthesis];
        }

        return string.Concat(culture.TextInfo.ToUpper(name[..1]), name[1..]);
    }
}
