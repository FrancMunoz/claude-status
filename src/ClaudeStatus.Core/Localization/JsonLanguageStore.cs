using System.Globalization;
using System.Text.Json;

namespace ClaudeStatus.Localization;

/// <summary>
/// Translations supplied as loose JSON files, which override the compiled ones.
/// </summary>
/// <remarks>
/// <para>
/// The point of this is that adding or fixing a language must not require the
/// .NET SDK. A translator copies <c>lang/en.json</c> to <c>lang/it.json</c>,
/// translates the values, restarts the app, and Italian appears in the language
/// list. Nothing is compiled and nothing is signed.
/// </para>
/// <para>
/// Because the files are user-supplied they are treated as untrusted input:
/// anything unreadable, oversized, or not a flat object of strings is ignored
/// and the built-in text is used instead. A broken translation file must never
/// be able to stop the app from starting.
/// </para>
/// </remarks>
public sealed class JsonLanguageStore
{
    /// <summary>The folder name, directly inside the config directory.</summary>
    public const string FolderName = "lang";

    /// <summary>
    /// Largest translation file we will read.
    /// </summary>
    /// <remarks>
    /// The real files are around 8 KB. A megabyte is generous enough that a
    /// legitimate file never hits it, and small enough that a corrupt or hostile
    /// one cannot exhaust memory.
    /// </remarks>
    public const int MaximumFileBytes = 1024 * 1024;

    /// <summary>
    /// Parsing rules for a hand-edited file.
    /// </summary>
    /// <remarks>
    /// Comments and trailing commas are allowed because a human maintains these by
    /// hand, and rejecting a file over a trailing comma would be needlessly hostile.
    /// </remarks>
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _directory;

    /// <param name="configDirectory">The app's config directory; <c>lang</c> sits inside it.</param>
    public JsonLanguageStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _directory = Path.Combine(configDirectory, FolderName);
    }

    /// <summary>The folder translators drop files into. Shown in the Config window.</summary>
    public string Directory => _directory;

    /// <summary>
    /// Language tags that have a usable file on disk.
    /// </summary>
    /// <remarks>
    /// Only the file name is inspected here, not the contents - listing the
    /// languages happens at startup and must stay cheap. A file that turns out to
    /// be unparseable simply contributes no overrides when it is loaded.
    /// </remarks>
    public IReadOnlyList<string> AvailableTags()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return [];
        }

        try
        {
            var tags = new List<string>();
            foreach (string path in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
            {
                string tag = Path.GetFileNameWithoutExtension(path);
                if (LanguageCatalog.IsValidTag(tag))
                {
                    tags.Add(tag);
                }
            }

            tags.Sort(StringComparer.OrdinalIgnoreCase);
            return tags;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Loads the overrides for a culture, walking up to its parent.
    /// </summary>
    /// <remarks>
    /// <c>es-AR.json</c> is preferred over <c>es.json</c> when both exist, matching
    /// how <see cref="System.Resources.ResourceManager"/> resolves satellite
    /// assemblies, so the two layers behave the same way.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Load(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        for (CultureInfo current = culture;
             !string.IsNullOrEmpty(current.Name);
             current = current.Parent)
        {
            IReadOnlyDictionary<string, string>? found = TryLoadExact(current.Name);
            if (found is not null)
            {
                return found;
            }
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private Dictionary<string, string>? TryLoadExact(string tag)
    {
        if (!LanguageCatalog.IsValidTag(tag))
        {
            return null;
        }

        // The tag is validated above, so it cannot escape the folder; combining an
        // unvalidated name here would be a path traversal.
        string path = Path.Combine(_directory, tag + ".json");

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > MaximumFileBytes)
            {
                return null;
            }

            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream, DocumentOptions);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                // Only flat string values are meaningful. A nested object or an
                // array is a mistake in the file, not something to guess about.
                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } value)
                {
                    map[property.Name] = value;
                }
            }

            return map;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
