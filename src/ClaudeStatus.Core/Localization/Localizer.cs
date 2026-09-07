using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace ClaudeStatus.Localization;

/// <summary>
/// The default <see cref="ILocalizer"/>: loose JSON files over compiled resources.
/// </summary>
/// <remarks>
/// <para>
/// Three layers are consulted for every key, in order:
/// </para>
/// <list type="number">
///   <item><description><c>lang/&lt;culture&gt;.json</c> in the config directory, if present.</description></item>
///   <item><description>The satellite assembly for the culture, e.g. <c>Strings.es.resx</c>.</description></item>
///   <item><description><c>Strings.resx</c>, which is English and always complete.</description></item>
/// </list>
/// <para>
/// The layering is what makes a partial translation safe. A JSON file with three
/// keys in it overrides exactly those three; everything else keeps falling through
/// to the compiled text. There is no "all or nothing" state where adding a file
/// blanks the interface.
/// </para>
/// </remarks>
public sealed class Localizer : ILocalizer
{
    private static readonly IReadOnlyDictionary<string, string> NoOverrides =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly ResourceManager _resources;
    private readonly JsonLanguageStore? _overrideStore;
    private IReadOnlyDictionary<string, string> _overrides = NoOverrides;
    private CultureInfo _culture = CultureInfo.GetCultureInfo("en");

    /// <param name="resources">The compiled resource set. Defaults to the app's own.</param>
    /// <param name="overrideStore">Loose translation files, or null to use only compiled ones.</param>
    public Localizer(ResourceManager? resources = null, JsonLanguageStore? overrideStore = null)
    {
        _resources = resources ?? Strings.ResourceManager;
        _overrideStore = overrideStore;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public CultureInfo Culture => _culture;

    /// <summary>
    /// Every language that has text available, built-in or dropped in as JSON.
    /// </summary>
    /// <remarks>
    /// Ordered with the built-in languages first, in the order they were shipped,
    /// then anything added locally. That keeps the Config window's list stable
    /// rather than reshuffling when a file appears.
    /// </remarks>
    public IReadOnlyList<string> AvailableTags()
    {
        var tags = new List<string>(LanguageCatalog.BuiltIn);
        if (_overrideStore is not null)
        {
            foreach (string tag in _overrideStore.AvailableTags())
            {
                if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add(tag);
                }
            }
        }

        return tags;
    }

    /// <summary>
    /// Switches language and tells every binding to re-read.
    /// </summary>
    /// <remarks>
    /// Also sets the thread's culture, so a percentage formatted anywhere in the
    /// app picks up the right decimal separator without each call site having to
    /// pass one. Parsing and persistence are unaffected: everything written to
    /// disk or sent over the wire already specifies <see cref="CultureInfo.InvariantCulture"/>
    /// explicitly, which CA1305 enforces across the build.
    /// </remarks>
    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        _culture = culture;
        _overrides = _overrideStore?.Load(culture) ?? NoOverrides;

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        // An empty name means "every property", which is how a binding to an
        // indexer is told that any key may now resolve differently.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    /// <inheritdoc />
    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            if (_overrides.TryGetValue(key, out string? overridden))
            {
                return overridden;
            }

            try
            {
                // ResourceManager walks es-AR -> es -> neutral by itself, so a key
                // missing from one translation still resolves to English.
                return _resources.GetString(key, _culture) ?? key;
            }
            catch (MissingManifestResourceException)
            {
                // Only reachable if the neutral resources were trimmed out of the
                // assembly. Showing keys is bad; crashing the UI thread is worse.
                return key;
            }
        }
    }

    /// <inheritdoc />
    public string Format(string key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string template = this[key];
        try
        {
            return string.Format(_culture, template, arguments);
        }
        catch (FormatException)
        {
            // A translator can leave a stray brace or an argument index we never
            // pass. That is their bug, but it must not take a window down, so the
            // raw template is shown instead - visibly wrong, and diagnosable.
            return template;
        }
    }
}
