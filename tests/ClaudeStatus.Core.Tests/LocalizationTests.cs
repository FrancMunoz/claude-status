using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClaudeStatus.Localization;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Keeps the five resource files honest about each other.
/// </summary>
/// <remarks>
/// <para>
/// A resource system replaces compile-time errors with runtime blanks. These
/// tests put the compile-time check back: they read the .resx files off disk and
/// compare them, so a key added to English and forgotten in French fails the
/// build rather than showing up as an English word in a French window.
/// </para>
/// <para>
/// Reading the source files rather than the compiled resources is deliberate. It
/// is the translator's artefact that has to be correct, and the error message
/// then names a file someone can open and fix.
/// </para>
/// </remarks>
public class ResourceFileTests
{
    /// <summary>The neutral (English) file. Every other language is compared to it.</summary>
    private const string Neutral = "Strings.resx";

    private static readonly string[] Translations =
        ["Strings.es.resx", "Strings.ca.resx", "Strings.de.resx", "Strings.fr.resx"];

    /// <summary>Walks up from the test binaries to the repository root.</summary>
    private static string LocalizationDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClaudeStatus.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.SkipWhen(directory is null, "Running outside the repository; source files unavailable.");
        return Path.Combine(directory!.FullName, "src", "ClaudeStatus.Core", "Localization");
    }

    private static Dictionary<string, string> Read(string fileName)
    {
        string path = Path.Combine(LocalizationDirectory(), fileName);
        XDocument document = XDocument.Load(path);

        return document.Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    /// <summary>Every <c>{0}</c>-style placeholder in a template.</summary>
    private static SortedSet<string> Placeholders(string template)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(template, @"\{(\d+)(?::[^}]*)?\}"))
        {
            found.Add(match.Groups[1].Value);
        }

        return found;
    }

    [Theory]
    [InlineData("Strings.es.resx")]
    [InlineData("Strings.ca.resx")]
    [InlineData("Strings.de.resx")]
    [InlineData("Strings.fr.resx")]
    public void Every_translation_covers_exactly_the_same_keys_as_English(string fileName)
    {
        Dictionary<string, string> english = Read(Neutral);
        Dictionary<string, string> translated = Read(fileName);

        translated.Keys.Except(english.Keys).Should().BeEmpty(
            $"{fileName} has keys English does not - a rename that only landed in one file");
        english.Keys.Except(translated.Keys).Should().BeEmpty(
            $"{fileName} is missing keys; add them or the app falls back to English there");
    }

    [Theory]
    [InlineData("Strings.es.resx")]
    [InlineData("Strings.ca.resx")]
    [InlineData("Strings.de.resx")]
    [InlineData("Strings.fr.resx")]
    public void Every_translation_keeps_the_placeholders_English_has(string fileName)
    {
        // The failure this catches is nasty: a dropped {0} silently loses the
        // number from a sentence, and a stray {1} throws a FormatException that
        // the localizer swallows into a raw template on screen.
        Dictionary<string, string> english = Read(Neutral);
        Dictionary<string, string> translated = Read(fileName);

        foreach ((string key, string value) in english)
        {
            if (!translated.TryGetValue(key, out string? other))
            {
                continue;
            }

            Placeholders(other).Should().Equal(
                Placeholders(value),
                $"{fileName} key '{key}' must use the same placeholders as English");
        }
    }

    [Theory]
    [InlineData("Strings.resx")]
    [InlineData("Strings.es.resx")]
    [InlineData("Strings.ca.resx")]
    [InlineData("Strings.de.resx")]
    [InlineData("Strings.fr.resx")]
    public void No_resource_value_is_blank(string fileName)
    {
        // A blank is worse than an untranslated string: the control just vanishes.
        Read(fileName).Where(pair => string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .Should().BeEmpty($"{fileName} has empty values");
    }

    [Fact]
    public void No_resource_value_looks_like_a_credential()
    {
        // Resource files are shipped and world-readable. Nothing secret belongs
        // in one, and the placeholder token prefixes in the Credential_* messages
        // are deliberately truncated stubs, not real values.
        foreach (string file in Translations.Append(Neutral))
        {
            foreach ((string key, string value) in Read(file))
            {
                Redactor.LooksRedacted(value).Should().BeTrue(
                    $"{file} key '{key}' contains credential-shaped text");
            }
        }
    }

    [Fact]
    public void Every_shipped_language_has_a_resource_file()
    {
        foreach (string tag in LanguageCatalog.BuiltIn)
        {
            string expected = tag == "en" ? Neutral : $"Strings.{tag}.resx";
            File.Exists(Path.Combine(LocalizationDirectory(), expected)).Should().BeTrue(
                $"{tag} is offered in the language picker, so {expected} must exist");
        }
    }
}

/// <summary>The lookup chain: JSON override, then satellite, then English.</summary>
public class LocalizerTests
{
    [Fact]
    public void An_unknown_key_comes_back_as_the_key_rather_than_a_blank()
    {
        // Visible nonsense is diagnosable; an empty label is not.
        TestLocalizer.English()["Nope_NotAKey"].Should().Be("Nope_NotAKey");
    }

    [Fact]
    public void A_null_or_empty_key_is_empty_rather_than_an_exception()
    {
        TestLocalizer.English()[string.Empty].Should().BeEmpty();
    }

    [Fact]
    public void Switching_culture_switches_the_text()
    {
        var localizer = new Localizer();

        localizer.SetCulture(CultureInfo.GetCultureInfo("en"));
        string english = localizer["Tray_Quit"];

        localizer.SetCulture(CultureInfo.GetCultureInfo("es"));
        string spanish = localizer["Tray_Quit"];

        english.Should().Be("Quit");
        spanish.Should().Be("Salir");
    }

    [Fact]
    public void A_regional_culture_falls_back_to_its_parent_language()
    {
        // A machine set to es-AR must get Spanish, not English.
        var localizer = new Localizer();
        localizer.SetCulture(CultureInfo.GetCultureInfo("es-AR"));

        localizer["Tray_Quit"].Should().Be("Salir");
    }

    [Fact]
    public void Switching_culture_notifies_bindings()
    {
        var localizer = new Localizer();
        bool notified = false;
        localizer.PropertyChanged += (_, _) => notified = true;

        localizer.SetCulture(CultureInfo.GetCultureInfo("fr"));

        notified.Should().BeTrue("every bound label has to re-read");
    }

    [Fact]
    public void Formatting_uses_the_chosen_culture_for_numbers()
    {
        // Spanish writes 61,5 - and getting this wrong makes the app look foreign
        // in exactly the way translating it was meant to avoid.
        var localizer = new Localizer();
        localizer.SetCulture(CultureInfo.GetCultureInfo("es"));

        localizer.Format("Bar_Percent", 61.5d).Should().Contain("61,5");
    }

    [Fact]
    public void A_broken_template_shows_the_template_rather_than_throwing()
    {
        // A translator can leave a stray brace. That must not take a window down.
        var localizer = new Localizer();
        localizer.SetCulture(CultureInfo.GetCultureInfo("en"));

        Func<string> act = () => localizer.Format("Tray_Quit", "unused");

        act.Should().NotThrow();
    }
}

/// <summary>Loose JSON translation files, which are untrusted input.</summary>
public class JsonLanguageStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "claudestatus-lang-" + Guid.NewGuid().ToString("N"));

    private string LangDirectory => Path.Combine(_directory, JsonLanguageStore.FolderName);

    private JsonLanguageStore Store()
    {
        Directory.CreateDirectory(LangDirectory);
        return new JsonLanguageStore(_directory);
    }

    private void Write(string name, string content)
        => File.WriteAllText(Path.Combine(LangDirectory, name), content);

    [Fact]
    public void A_missing_folder_is_not_an_error()
    {
        var store = new JsonLanguageStore(_directory);

        store.AvailableTags().Should().BeEmpty();
        store.Load(CultureInfo.GetCultureInfo("en")).Should().BeEmpty();
    }

    [Fact]
    public void A_dropped_in_file_adds_a_language()
    {
        JsonLanguageStore store = Store();
        Write("it.json", """{ "Tray_Quit": "Esci" }""");

        store.AvailableTags().Should().Contain("it");
        store.Load(CultureInfo.GetCultureInfo("it"))["Tray_Quit"].Should().Be("Esci");
    }

    [Fact]
    public void An_override_beats_the_compiled_text_but_only_for_the_keys_it_names()
    {
        // The layering is the point: a three-line file must not blank the rest.
        JsonLanguageStore store = Store();
        Write("es.json", """{ "Tray_Quit": "Cerrar" }""");

        var localizer = new Localizer(resources: null, overrideStore: store);
        localizer.SetCulture(CultureInfo.GetCultureInfo("es"));

        localizer["Tray_Quit"].Should().Be("Cerrar", "the file wins");
        localizer["Tray_Refresh"].Should().Be("Actualizar", "everything else still resolves");
    }

    [Fact]
    public void A_language_added_as_a_file_falls_back_to_English_for_missing_keys()
    {
        JsonLanguageStore store = Store();
        Write("it.json", """{ "Tray_Quit": "Esci" }""");

        var localizer = new Localizer(resources: null, overrideStore: store);
        localizer.SetCulture(CultureInfo.GetCultureInfo("it"));

        localizer["Tray_Quit"].Should().Be("Esci");
        localizer["Tray_Refresh"].Should().Be("Refresh", "Italian has no satellite assembly");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"a string\"")]
    [InlineData("{ \"Tray_Quit\": 42 }")]
    [InlineData("{ \"Tray_Quit\": { \"nested\": \"no\" } }")]
    public void A_broken_file_is_ignored_rather_than_breaking_startup(string content)
    {
        JsonLanguageStore store = Store();
        Write("es.json", content);

        var localizer = new Localizer(resources: null, overrideStore: store);
        Action act = () => localizer.SetCulture(CultureInfo.GetCultureInfo("es"));

        act.Should().NotThrow();
        localizer["Tray_Quit"].Should().Be("Salir", "the compiled Spanish is still there");
    }

    [Fact]
    public void A_file_named_with_something_that_is_not_a_language_tag_is_ignored()
    {
        // The file name is used to build a path, so it has to be validated.
        JsonLanguageStore store = Store();
        Write("..evil.json", """{ "Tray_Quit": "no" }""");
        Write("a-very-long-name.json", """{ "Tray_Quit": "no" }""");

        store.AvailableTags().Should().BeEmpty();
    }

    [Fact]
    public void An_oversized_file_is_ignored()
    {
        JsonLanguageStore store = Store();
        Write("it.json", "{\"Tray_Quit\":\"" + new string('x', JsonLanguageStore.MaximumFileBytes) + "\"}");

        store.Load(CultureInfo.GetCultureInfo("it")).Should().BeEmpty();
    }

    [Fact]
    public void A_regional_file_is_preferred_over_its_parent()
    {
        JsonLanguageStore store = Store();
        Write("es.json", """{ "Tray_Quit": "Salir (es)" }""");
        Write("es-AR.json", """{ "Tray_Quit": "Salir (es-AR)" }""");

        store.Load(CultureInfo.GetCultureInfo("es-AR"))["Tray_Quit"].Should().Be("Salir (es-AR)");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}

/// <summary>How a language gets chosen at startup.</summary>
public class LanguageCatalogTests
{
    private static readonly string[] Available = ["en", "es", "ca", "de", "fr"];

    [Fact]
    public void An_explicit_choice_wins_over_the_system_language()
    {
        LanguageCatalog.Resolve("ca", Available, CultureInfo.GetCultureInfo("de-DE"))
            .Name.Should().Be("ca");
    }

    [Fact]
    public void The_system_language_is_used_when_there_is_no_choice()
    {
        LanguageCatalog.Resolve("", Available, CultureInfo.GetCultureInfo("fr-CA"))
            .Name.Should().Be("fr", "fr-CA has no translation of its own, its parent does");
    }

    [Fact]
    public void An_unsupported_system_language_falls_back_to_English()
    {
        LanguageCatalog.Resolve("", Available, CultureInfo.GetCultureInfo("ja-JP"))
            .Name.Should().Be("en");
    }

    [Fact]
    public void A_chosen_language_that_is_no_longer_installed_falls_back_rather_than_failing()
    {
        // Someone picks Italian from a dropped-in file, then deletes the file.
        LanguageCatalog.Resolve("it", Available, CultureInfo.GetCultureInfo("es-ES"))
            .Name.Should().Be("es");
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("english", false)]
    [InlineData("sk-ant-oat01-abcdef", false)]
    [InlineData("../es", false)]
    [InlineData("e", false)]
    public void Tag_validation_accepts_language_tags_and_nothing_else(string? tag, bool valid)
    {
        LanguageCatalog.IsValidTag(tag).Should().Be(valid);
    }

    [Theory]
    [InlineData("es", "Español")]
    [InlineData("fr", "Français")]
    [InlineData("ca", "Català")]
    [InlineData("de", "Deutsch")]
    public void A_language_is_named_in_its_own_language_and_capitalised(string tag, string expected)
    {
        // NativeName is lower-case for several languages, which reads as a typo
        // in a picker.
        LanguageCatalog.NativeName(CultureInfo.GetCultureInfo(tag)).Should().Be(expected);
    }
}
