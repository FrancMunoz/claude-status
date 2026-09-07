using ClaudeStatus.Theming;

namespace ClaudeStatus.Core.Tests;

/// <summary>Hex parsing, blending and contrast maths.</summary>
public class RgbTests
{
    [Theory]
    [InlineData("#FF8A3D", 0xFF, 0x8A, 0x3D)]
    [InlineData("ff8a3d", 0xFF, 0x8A, 0x3D)]
    [InlineData("  #FF8A3D  ", 0xFF, 0x8A, 0x3D)]
    [InlineData("#f0a", 0xFF, 0x00, 0xAA)]
    [InlineData("#000", 0x00, 0x00, 0x00)]
    public void Hex_parses_in_both_lengths_and_either_case(string hex, int r, int g, int b)
    {
        Rgb.TryParse(hex, out Rgb value).Should().BeTrue();
        value.Should().Be(new Rgb((byte)r, (byte)g, (byte)b));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#GGGGGG")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("rgb(1,2,3)")]
    public void Anything_that_is_not_a_hex_colour_is_rejected(string? hex)
    {
        Rgb.TryParse(hex, out _).Should().BeFalse();
    }

    [Fact]
    public void A_bad_colour_in_a_theme_file_costs_that_colour_not_the_app()
    {
        var fallback = new Rgb(1, 2, 3);

        Rgb.ParseOr("not a colour", fallback).Should().Be(fallback);
        Rgb.ParseOr("#FFFFFF", fallback).Should().Be(new Rgb(255, 255, 255));
    }

    [Fact]
    public void Round_trips_through_hex()
    {
        var colour = new Rgb(0x4C, 0x8D, 0xFF);

        colour.ToHex().Should().Be("#4C8DFF");
        Rgb.ParseOr(colour.ToHex(), default).Should().Be(colour);
    }

    [Fact]
    public void Luminance_uses_the_sRGB_curve_not_a_naive_average()
    {
        // The point of the gamma step: mid-blue and mid-yellow have the same
        // arithmetic mean and are nowhere near equally bright.
        var blue = new Rgb(0x00, 0x00, 0xFF);
        var yellow = new Rgb(0xFF, 0xFF, 0x00);

        yellow.RelativeLuminance.Should().BeGreaterThan(blue.RelativeLuminance * 5);
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x00, true)]
    [InlineData(0x16, 0x18, 0x1D, true)]
    [InlineData(0xFF, 0xFF, 0xFF, false)]
    [InlineData(0xF7, 0xF4, 0xEE, false)]
    public void Darkness_is_decided_by_luminance(int r, int g, int b, bool expected)
    {
        new Rgb((byte)r, (byte)g, (byte)b).IsDark.Should().Be(expected);
    }

    [Fact]
    public void Blending_moves_between_the_two_ends()
    {
        var black = new Rgb(0, 0, 0);
        var white = new Rgb(255, 255, 255);

        black.Blend(white, 0d).Should().Be(black);
        black.Blend(white, 1d).Should().Be(white);
        black.Blend(white, 0.5d).Should().Be(new Rgb(128, 128, 128));
    }

    [Fact]
    public void Blending_clamps_rather_than_extrapolating()
    {
        var black = new Rgb(0, 0, 0);
        var white = new Rgb(255, 255, 255);

        black.Blend(white, -1d).Should().Be(black);
        black.Blend(white, 5d).Should().Be(white);
    }

    [Fact]
    public void Contrast_matches_the_WCAG_extremes()
    {
        var black = new Rgb(0, 0, 0);
        var white = new Rgb(255, 255, 255);

        black.ContrastWith(white).Should().BeApproximately(21d, 0.05d);
        black.ContrastWith(black).Should().Be(1d);
    }
}

/// <summary>
/// Every shipped theme has to be legible, not merely pretty.
/// </summary>
/// <remarks>
/// These are the tests that stop a nice-looking palette shipping with body text
/// nobody can read. WCAG asks 4.5:1 for body text and 3:1 for large or secondary
/// text; the muted grey is derived rather than declared, so it is checked too.
/// </remarks>
public class ThemeCatalogTests
{
    public static TheoryData<string> BuiltInIds()
    {
        var data = new TheoryData<string>();
        foreach (Theme theme in ThemeCatalog.BuiltIn)
        {
            data.Add(theme.Id);
        }

        return data;
    }

    private static Theme ById(string id) => ThemeCatalog.BuiltIn.Single(t => t.Id == id);

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void Body_text_clears_the_WCAG_threshold(string id)
    {
        ById(id).TextContrast.Should().BeGreaterThanOrEqualTo(
            4.5d, $"'{id}' body text must be readable");
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void The_derived_grey_stays_readable(string id)
    {
        // 3:1 rather than 4.5: this is secondary text, and holding it to the body
        // threshold would leave no visual difference between the two at all.
        ById(id).MutedContrast.Should().BeGreaterThanOrEqualTo(
            3d, $"'{id}' captions must be readable, not decorative");
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void The_primary_and_alert_colours_are_visible_on_the_background(string id)
    {
        Theme theme = ById(id);

        theme.Primary.ContrastWith(theme.Background).Should().BeGreaterThanOrEqualTo(
            3d, $"'{id}' headings use the primary colour");
        theme.Alert.ContrastWith(theme.Background).Should().BeGreaterThanOrEqualTo(
            3d, $"'{id}' warnings must not be a subtle hint");
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void The_alert_colour_is_distinguishable_from_the_primary(string id)
    {
        // Otherwise "over threshold" looks exactly like a heading.
        //
        // Measured as channel distance, not contrast ratio. Contrast is a
        // brightness comparison: a blue primary and a red alert of similar
        // lightness score about 1.06 while being impossible to confuse. The first
        // version of this test used contrast and failed three themes that were
        // perfectly fine.
        Theme theme = ById(id);

        Distance(theme.Alert, theme.Primary).Should().BeGreaterThan(
            60d, $"'{id}' must not use near-identical colours for accents and alarms");
    }

    /// <summary>Straight-line distance between two colours in RGB space.</summary>
    private static double Distance(Rgb a, Rgb b)
    {
        double dr = a.R - b.R;
        double dg = a.G - b.G;
        double db = a.B - b.B;
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void A_filled_accent_button_is_legible_in_every_theme(string id)
    {
        // OnPrimary is the label on a filled accent button. Picking black or white
        // by the primary's own luminance is only correct if it actually clears the
        // body-text threshold on all eleven - a mid-tone accent is where that goes
        // wrong, and the Save button is not a place to discover it.
        Theme theme = ById(id);

        theme.OnPrimary.ContrastWith(theme.Primary).Should().BeGreaterThanOrEqualTo(
            4.5d, $"'{id}' puts button labels on its primary colour");
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void The_bar_track_is_visible_but_quieter_than_the_bar(string id)
    {
        // A track that vanishes into the background turns a 20 % reading into a
        // short line floating in space, with nothing to read it against; a track
        // as strong as the fill turns every bar into a full one at a glance.
        Theme theme = ById(id);

        Distance(theme.Track, theme.Background).Should().BeGreaterThan(
            20d, $"'{id}' must show where an unfilled bar ends");
        theme.Track.ContrastWith(theme.Background).Should().BeLessThan(
            theme.Primary.ContrastWith(theme.Background),
            $"'{id}' must not let the groove compete with the fill");
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void The_muted_grey_is_dimmer_than_the_body_text(string id)
    {
        // The whole point of deriving it: it has to read as secondary.
        Theme theme = ById(id);

        theme.MutedContrast.Should().BeLessThan(theme.TextContrast);
    }

    [Fact]
    public void Ids_are_unique_and_valid()
    {
        string[] ids = [.. ThemeCatalog.BuiltIn.Select(t => t.Id)];

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().AllSatisfy(id => ThemeCatalog.IsValidId(id).Should().BeTrue());
    }

    [Fact]
    public void Light_and_dark_are_present_because_the_system_theme_resolves_to_them()
    {
        ThemeCatalog.BuiltIn.Should().Contain(ThemeCatalog.Light);
        ThemeCatalog.BuiltIn.Should().Contain(ThemeCatalog.Dark);
        ThemeCatalog.Light.IsDark.Should().BeFalse();
        ThemeCatalog.Dark.IsDark.Should().BeTrue();
    }

    [Theory]
    [InlineData("dark", false, "dark")]
    [InlineData("claude", true, "claude")]
    [InlineData("", true, "dark")]
    [InlineData("", false, "light")]
    [InlineData("system", true, "dark")]
    [InlineData("system", false, "light")]
    [InlineData("nonexistent", true, "dark")]
    public void Resolution_prefers_the_choice_then_falls_back_to_the_system(
        string id, bool systemIsDark, string expected)
    {
        ThemeCatalog.Resolve(id, ThemeCatalog.BuiltIn, systemIsDark)
            .Id.Should().Be(expected);
    }

    [Theory]
    [InlineData("dark", true)]
    [InlineData("my-theme_2", true)]
    [InlineData("Dark", false)]
    [InlineData("2dark", false)]
    [InlineData("../evil", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Id_validation_accepts_ids_and_nothing_else(string? id, bool valid)
    {
        ThemeCatalog.IsValidId(id).Should().Be(valid);
    }
}

/// <summary>Theme files are untrusted input.</summary>
public class JsonThemeStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "claudestatus-themes-" + Guid.NewGuid().ToString("N"));

    private string ThemesDirectory => Path.Combine(_directory, JsonThemeStore.FolderName);

    private JsonThemeStore Store()
    {
        Directory.CreateDirectory(ThemesDirectory);
        return new JsonThemeStore(_directory);
    }

    private void Write(string name, string content)
        => File.WriteAllText(Path.Combine(ThemesDirectory, name), content);

    private const string Valid = """
        {
          "name": "Midnight",
          "primary": "#7AA2F7",
          "background": "#1A1B26",
          "text": "#C0CAF5",
          "alert": "#F7768E"
        }
        """;

    [Fact]
    public void A_missing_folder_leaves_only_the_built_in_themes()
    {
        var store = new JsonThemeStore(_directory);

        store.Load().Should().BeEmpty();
        store.All().Should().BeEquivalentTo(ThemeCatalog.BuiltIn);
    }

    [Fact]
    public void A_dropped_in_file_becomes_a_theme_named_after_the_file()
    {
        JsonThemeStore store = Store();
        Write("midnight.json", Valid);

        Theme theme = store.Load().Single();

        theme.Id.Should().Be("midnight");
        theme.Name.Should().Be("Midnight");
        theme.Background.Should().Be(new Rgb(0x1A, 0x1B, 0x26));
        theme.IsDark.Should().BeTrue("it is derived from the background, not declared");
    }

    [Fact]
    public void A_file_named_after_a_built_in_replaces_it_in_place()
    {
        // This is how someone tweaks the shipped Dark theme without forking. It
        // must keep its position so the picker does not reshuffle.
        JsonThemeStore store = Store();
        Write("dark.json", Valid);

        IReadOnlyList<Theme> all = store.All();

        all.Should().HaveCount(ThemeCatalog.BuiltIn.Count);
        all[1].Id.Should().Be("dark");
        all[1].Name.Should().Be("Midnight");
    }

    [Fact]
    public void A_new_file_is_appended_after_the_built_ins()
    {
        JsonThemeStore store = Store();
        Write("midnight.json", Valid);

        store.All()[^1].Id.Should().Be("midnight");
    }

    [Fact]
    public void A_file_with_no_name_falls_back_to_its_file_name()
    {
        JsonThemeStore store = Store();
        Write("midnight.json", """
            { "primary": "#7AA2F7", "background": "#1A1B26", "text": "#C0CAF5", "alert": "#F7768E" }
            """);

        store.Load().Single().Name.Should().Be("midnight");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    [InlineData("""{ "primary": "#FFF", "background": "#000", "text": "#FFF" }""")]
    [InlineData("""{ "primary": "nope", "background": "#000", "text": "#FFF", "alert": "#F00" }""")]
    public void A_file_missing_or_mangling_a_colour_is_skipped_entirely(string content)
    {
        // Partly applying it would look like a rendering bug rather than a bad file.
        JsonThemeStore store = Store();
        Write("broken.json", content);

        store.Load().Should().BeEmpty();
        store.All().Should().BeEquivalentTo(ThemeCatalog.BuiltIn);
    }

    [Fact]
    public void A_file_whose_name_is_not_a_valid_id_is_ignored()
    {
        // The name becomes a path segment, so it has to be validated.
        JsonThemeStore store = Store();
        Write("..evil.json", Valid);
        Write("Upper.json", Valid);

        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void An_oversized_file_is_ignored()
    {
        JsonThemeStore store = Store();
        Write("huge.json", "{\"name\":\"" + new string('x', JsonThemeStore.MaximumFileBytes) + "\"}");

        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void A_name_from_a_file_is_bounded_so_it_cannot_break_the_picker()
    {
        JsonThemeStore store = Store();
        Write("long.json", $$"""
            {
              "name": "{{new string('N', 200)}}",
              "primary": "#7AA2F7", "background": "#1A1B26",
              "text": "#C0CAF5", "alert": "#F7768E"
            }
            """);

        store.Load().Single().Name.Length.Should().BeLessThanOrEqualTo(40);
    }

    [Fact]
    public void A_theme_loaded_from_a_file_is_selectable()
    {
        JsonThemeStore store = Store();
        Write("midnight.json", Valid);

        ThemeCatalog.Resolve("midnight", store.All(), systemIsDark: false)
            .Name.Should().Be("Midnight");
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

/// <summary>The font family field, which is the loosest thing in settings.json.</summary>
public class FontCatalogTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Segoe UI")]
    [InlineData("Segoe UI, SF Pro Text, Ubuntu, Noto Sans")]
    [InlineData("Iowan Old Style")]
    [InlineData("Helvetica-Bold")]
    public void A_real_font_stack_is_accepted(string family)
    {
        FontCatalog.IsValidFamily(family).Should().BeTrue();
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("Segoe<script>")]
    [InlineData("font;name")]
    [InlineData("family\nname")]
    [InlineData("   ")]
    [InlineData("sk-ant-oat01-abcdefghijklmnopqrstuvwxyz")]
    public void Anything_that_is_not_a_font_stack_is_rejected(string family)
    {
        FontCatalog.IsValidFamily(family).Should().BeFalse();
    }

    [Fact]
    public void A_family_longer_than_the_cap_is_rejected()
    {
        FontCatalog.IsValidFamily(new string('A', FontCatalog.MaximumLength + 1))
            .Should().BeFalse();
    }

    [Fact]
    public void Normalising_an_invalid_family_gives_the_platform_default()
    {
        FontCatalog.Normalize("bad;value").Should().Be(FontCatalog.SystemDefault);
        FontCatalog.Normalize(null).Should().Be(FontCatalog.SystemDefault);
        FontCatalog.Normalize("  Segoe UI  ").Should().Be("Segoe UI");
    }

    [Fact]
    public void Every_suggestion_is_itself_valid()
    {
        // A shipped suggestion that the validator rejects would be selectable and
        // then silently discarded on save.
        FontCatalog.Suggested.Should().AllSatisfy(
            family => FontCatalog.IsValidFamily(family).Should().BeTrue());
    }
}
