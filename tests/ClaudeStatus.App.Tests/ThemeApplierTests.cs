using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using ClaudeStatus.App.Theming;
using ClaudeStatus.Theming;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// What a theme actually puts into the application's resources.
/// </summary>
/// <remarks>
/// These assert on the brushes rather than on rendered pixels, because the
/// headless renderer composites onto an opaque black backdrop: a fully
/// transparent panel and an opaque near-black one produce the same screenshot.
/// The alpha channel is the thing under test, so the alpha channel is what is
/// asserted.
/// </remarks>
[Collection(HeadlessTests.Name)]
public class ThemeApplierTests(HeadlessAppFixture fixture)
{
    /// <summary>Brushes that must never carry the OSD alpha.</summary>
    private static readonly string[] OpaqueBrushKeys =
    [
        "Theme.Background", "Theme.Surface", "Theme.Border",
        "Theme.Text", "Theme.Muted", "Theme.Primary", "Theme.Alert",
    ];

    /// <summary>The accent slots Fluent reads for its own controls.</summary>
    private static readonly string[] AccentKeys =
    [
        "SystemAccentColor",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
    ];

    private static Color ColorOf(string key)
    {
        Application.Current!.Resources.TryGetResource(key, null, out object? value)
            .Should().BeTrue($"'{key}' must be written by ThemeApplier");

        return value.Should().BeOfType<SolidColorBrush>().Subject.Color;
    }

    private static void Apply(Theme theme, double transparency = 0d, string font = "")
        => ThemeApplier.Apply(Application.Current!, theme, font, transparency);

    [Theory]
    [InlineData(0d, 255)]
    [InlineData(0.5d, 128)]
    [InlineData(0.65d, 89)]
    [InlineData(1d, 0)]
    public void The_popup_background_alpha_is_the_inverse_of_the_transparency(
        double transparency, int expected)
    {
        // The setting is transparency; a colour channel is an opacity. This is the
        // only place the two meet, so this is where the inversion is pinned.
        fixture.Should().NotBeNull();

        HeadlessAppFixture.Invoke(() =>
        {
            Apply(ThemeCatalog.Dark, transparency);

            ColorOf("Theme.OsdBackground").A.Should().Be((byte)expected);
        });
    }

    [Fact]
    public void The_default_transparency_is_a_barely_translucent_panel()
    {
        // The default is 10 %, not solid: enough that the popup reads as an
        // overlay, little enough that nothing behind it competes with the
        // numbers. Only the background takes the alpha, so the text is untouched.
        HeadlessAppFixture.Invoke(() =>
        {
            Apply(ThemeCatalog.Dark, ClaudeStatus.Config.AppSettings.DefaultOsdTransparency);

            ColorOf("Theme.OsdBackground").A.Should().Be(230, "255 less 10 %");
            ColorOf("Theme.OsdBorder").A.Should().Be(230, "the border fades with the panel");
            ColorOf("Theme.Text").A.Should().Be(255, "text never carries the alpha");
        });
    }

    [Fact]
    public void The_popup_border_fades_with_the_panel()
    {
        // Otherwise a fully transparent panel leaves a rounded rectangle drawn
        // around nothing, which reads as a rendering fault.
        HeadlessAppFixture.Invoke(() =>
        {
            Apply(ThemeCatalog.Dark, 1d);
            ColorOf("Theme.OsdBorder").A.Should().Be(0);

            Apply(ThemeCatalog.Dark, 0d);
            ColorOf("Theme.OsdBorder").A.Should().Be(255);
        });
    }

    [Fact]
    public void Nothing_except_the_popup_is_ever_translucent()
    {
        // The report and config windows are for reading. Only the OSD fades.
        HeadlessAppFixture.Invoke(() =>
        {
            Apply(ThemeCatalog.Dark, 1d);

            foreach (string key in OpaqueBrushKeys)
            {
                ColorOf(key).A.Should().Be(255, $"'{key}' is not part of the OSD fade");
            }
        });
    }

    [Fact]
    public void The_theme_colours_reach_the_resources_unchanged()
    {
        HeadlessAppFixture.Invoke(() =>
        {
            Theme theme = ThemeCatalog.BuiltIn.Single(t => t.Id == "nebula");
            Apply(theme);

            ColorOf("Theme.Background").Should().Be(
                Color.FromRgb(theme.Background.R, theme.Background.G, theme.Background.B));
            ColorOf("Theme.Primary").Should().Be(
                Color.FromRgb(theme.Primary.R, theme.Primary.G, theme.Primary.B));
            ColorOf("Theme.Muted").Should().Be(
                Color.FromRgb(theme.Muted.R, theme.Muted.G, theme.Muted.B));
        });
    }

    [Fact]
    public void A_dark_theme_selects_the_dark_control_variant()
    {
        // This is what makes buttons, sliders and the combo box match rather than
        // staying in whatever variant the app started in.
        HeadlessAppFixture.Invoke(() =>
        {
            Apply(ThemeCatalog.Dark);
            Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);

            Apply(ThemeCatalog.Light);
            Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Light);
        });
    }

    [Fact]
    public void The_accent_slots_are_filled_so_Fluent_controls_take_the_primary()
    {
        HeadlessAppFixture.Invoke(() =>
        {
            Theme theme = ThemeCatalog.BuiltIn.Single(t => t.Id == "aurora");
            Apply(theme);

            foreach (string key in AccentKeys)
            {
                Application.Current!.Resources.TryGetResource(key, null, out object? value)
                    .Should().BeTrue($"Fluent reads '{key}'");
                value.Should().BeOfType<Color>();
            }

            Application.Current!.Resources.TryGetResource("SystemAccentColor", null, out object? accent);
            accent.Should().Be(Color.FromRgb(theme.Primary.R, theme.Primary.G, theme.Primary.B));
        });
    }

    [Fact]
    public void An_empty_font_family_means_the_platform_default()
    {
        HeadlessAppFixture.Invoke(() =>
        {
            Apply(ThemeCatalog.Dark, font: string.Empty);

            Application.Current!.Resources.TryGetResource("Theme.FontFamily", null, out object? value);
            value.Should().Be(FontFamily.Default);
        });
    }

    [Fact]
    public void A_named_font_family_reaches_the_resources()
    {
        HeadlessAppFixture.Invoke(() =>
        {
            Apply(ThemeCatalog.Dark, font: "Cascadia Mono, Menlo");

            Application.Current!.Resources.TryGetResource("Theme.FontFamily", null, out object? value);
            value.Should().BeOfType<FontFamily>()
                .Subject.Name.Should().Be("Cascadia Mono");
        });
    }
}
