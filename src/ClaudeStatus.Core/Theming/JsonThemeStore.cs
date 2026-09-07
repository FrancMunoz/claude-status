using System.Text.Json;

namespace ClaudeStatus.Theming;

/// <summary>
/// Themes supplied as loose JSON files, alongside the built-in ones.
/// </summary>
/// <remarks>
/// <para>
/// Same contract as <see cref="Localization.JsonLanguageStore"/>: drop a file in,
/// restart, and it appears in the picker. The file name is the id, so
/// <c>themes/midnight.json</c> becomes the theme <c>midnight</c>.
/// </para>
/// <para>
/// A file that names a built-in id replaces it. That is deliberate - it is how
/// someone tweaks the shipped Dark theme without forking the app - and it is why
/// the built-in list is consulted after the files rather than before.
/// </para>
/// <para>
/// The files are untrusted input: anything unreadable, oversized, or missing a
/// colour is skipped, and a broken theme file can never stop the app starting.
/// </para>
/// </remarks>
public sealed class JsonThemeStore
{
    /// <summary>The folder name, directly inside the config directory.</summary>
    public const string FolderName = "themes";

    /// <summary>Largest theme file we will read. A real one is a few hundred bytes.</summary>
    public const int MaximumFileBytes = 64 * 1024;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _directory;

    /// <param name="configDirectory">The app's config directory; <c>themes</c> sits inside it.</param>
    public JsonThemeStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _directory = Path.Combine(configDirectory, FolderName);
    }

    /// <summary>The folder to drop theme files into. Shown in the Config window.</summary>
    public string Directory => _directory;

    /// <summary>
    /// Every theme on disk, in file-name order.
    /// </summary>
    /// <remarks>
    /// Never throws. A missing folder, an unreadable file and a file full of
    /// nonsense all mean the same thing here: no theme from that file.
    /// </remarks>
    public IReadOnlyList<Theme> Load()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return [];
        }

        try
        {
            var themes = new List<Theme>();
            foreach (string path in System.IO.Directory.EnumerateFiles(_directory, "*.json").Order(StringComparer.Ordinal))
            {
                if (TryLoad(path) is { } theme)
                {
                    themes.Add(theme);
                }
            }

            return themes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Merges file themes over the built-in ones.
    /// </summary>
    /// <remarks>
    /// A file wins over a built-in with the same id, and the built-in's position in
    /// the list is kept so replacing Dark does not move it to the bottom of the
    /// picker.
    /// </remarks>
    public IReadOnlyList<Theme> All()
    {
        IReadOnlyList<Theme> fromFiles = Load();
        if (fromFiles.Count == 0)
        {
            return ThemeCatalog.BuiltIn;
        }

        var byId = new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (Theme theme in ThemeCatalog.BuiltIn.Concat(fromFiles))
        {
            if (!byId.ContainsKey(theme.Id))
            {
                order.Add(theme.Id);
            }

            byId[theme.Id] = theme;
        }

        return [.. order.Select(id => byId[id])];
    }

    private static Theme? TryLoad(string path)
    {
        string id = Path.GetFileNameWithoutExtension(path);
        if (!ThemeCatalog.IsValidId(id))
        {
            return null;
        }

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

            JsonElement root = document.RootElement;

            // Every colour is required. A theme missing one would silently inherit
            // whatever was there before, which looks like a rendering bug rather
            // than a broken file.
            if (!Rgb.TryParse(ReadString(root, "primary"), out Rgb primary)
                || !Rgb.TryParse(ReadString(root, "background"), out Rgb background)
                || !Rgb.TryParse(ReadString(root, "text"), out Rgb text)
                || !Rgb.TryParse(ReadString(root, "alert"), out Rgb alert))
            {
                return null;
            }

            string name = ReadString(root, "name") is { Length: > 0 } given
                ? Trim(given)
                : id;

            return new Theme(id, name, primary, background, text, alert);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Bounds a name from an untrusted file so it cannot break the picker's layout.</summary>
    private static string Trim(string name)
    {
        string collapsed = new([.. name.Where(c => !char.IsControl(c))]);
        return collapsed.Length <= 40 ? collapsed : collapsed[..40];
    }

    private static string? ReadString(JsonElement owner, string name)
        => owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
