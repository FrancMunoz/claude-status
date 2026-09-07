using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using ClaudeStatus.Theming;

namespace ClaudeStatus.App.Theming;

/// <summary>
/// Pushes a <see cref="Theme"/> into the application's resources.
/// </summary>
/// <remarks>
/// <para>
/// Two things happen here, and the second is what makes it look finished:
/// </para>
/// <list type="number">
///   <item><description>
///     Our own <c>Theme.*</c> brushes, which the styles and views bind to with
///     <c>DynamicResource</c>. Changing one re-renders every window live, with no
///     reload and no reopening.
///   </description></item>
///   <item><description>
///     Fluent's own knobs: <see cref="ThemeVariant"/> so built-in controls render
///     light or dark, and <c>SystemAccentColor</c> so buttons, sliders, checkboxes
///     and the combo box take the theme's primary colour. Restyling every Fluent
///     control by hand would be an enormous amount of AXAML for the same result.
///   </description></item>
/// </list>
/// <para>
/// The tray icon is untouched. It lives on a taskbar this app does not theme, and
/// it keeps its own contrast logic.
/// </para>
/// </remarks>
public static class ThemeApplier
{
    /// <summary>Resource key prefix, so a stray key is obvious in a search.</summary>
    private const string Prefix = "Theme.";

    /// <summary>Applies a theme and an OSD opacity to the running application.</summary>
    /// <param name="application">The app whose resources are written.</param>
    /// <param name="theme">The colours.</param>
    /// <param name="fontFamily">A family or stack, or empty for the platform default.</param>
    /// <param name="osdTransparency">
    /// Background transparency for the details popup, 0 (solid) to 1 (invisible).
    /// </param>
    public static void Apply(
        Application application, Theme theme, string fontFamily, double osdTransparency)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(theme);

        // The one place in the app where transparency becomes alpha. Everything
        // above this line - the setting, the view model, the slider, the label -
        // speaks in transparency, so the word never has to be inverted in the
        // reader's head; a colour channel has to be an opacity, so it is converted
        // exactly once, here.
        double osdOpacity = 1d - Math.Clamp(osdTransparency, 0d, 1d);

        IResourceDictionary resources = application.Resources;

        // Fluent renders its own controls from this, so it has to change first:
        // setting it after the brushes would repaint twice.
        application.RequestedThemeVariant = theme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        SetBrush(resources, "Background", theme.Background);
        SetBrush(resources, "Surface", theme.Surface);
        SetBrush(resources, "Border", theme.Border);
        SetBrush(resources, "Track", theme.Track);
        SetBrush(resources, "Text", theme.Text);
        SetBrush(resources, "Muted", theme.Muted);
        SetBrush(resources, "Primary", theme.Primary);
        SetBrush(resources, "OnPrimary", theme.OnPrimary);
        SetBrush(resources, "Alert", theme.Alert);
        SetBrush(resources, "AlertSurface", theme.AlertSurface);
        SetBrush(resources, "AlertBorder", theme.AlertBorder);

        // The popup's background carries the alpha; its text does not. Anything
        // that dimmed the whole window would make the numbers hard to read, which
        // is the one thing the popup exists to avoid.
        resources[Prefix + "OsdBackground"] = new SolidColorBrush(
            ToColor(theme.Background, osdOpacity));

        // The border fades with the panel. At zero opacity an opaque outline would
        // leave a rectangle drawn around nothing, which looks like a rendering
        // fault rather than the intended "text floating on the desktop".
        resources[Prefix + "OsdBorder"] = new SolidColorBrush(
            ToColor(theme.Border, osdOpacity));

        resources[Prefix + "FontFamily"] = string.IsNullOrWhiteSpace(fontFamily)
            ? FontFamily.Default
            : new FontFamily(fontFamily);

        ApplyAccent(resources, theme);
        ApplyButtons(resources, theme);
        ApplyFields(resources, theme);
    }

    /// <summary>
    /// Repaints Fluent's input controls in the theme's own colours.
    /// </summary>
    /// <remarks>
    /// Left alone, a text box keeps Fluent's stock grey, which on a warm or
    /// saturated theme reads as a hole punched in the window. The token field in
    /// Config is the most consequential control in the app, so it should not be
    /// the one that looks unfinished.
    /// </remarks>
    private static void ApplyFields(IResourceDictionary resources, Theme theme)
    {
        Rgb rest = theme.Surface;
        Rgb hover = theme.Background.Blend(theme.Text, theme.IsDark ? 0.12d : 0.08d);

        Set(resources, "TextControlBackground", rest);
        Set(resources, "TextControlBackgroundPointerOver", hover);

        // Focused fields go back to the plain background: with the accent border
        // around them, a tinted fill as well is one signal too many.
        Set(resources, "TextControlBackgroundFocused", theme.Background);
        Set(resources, "TextControlBackgroundDisabled", theme.Background.Blend(theme.Text, 0.04d));

        Set(resources, "TextControlForeground", theme.Text);
        Set(resources, "TextControlForegroundPointerOver", theme.Text);
        Set(resources, "TextControlForegroundFocused", theme.Text);
        Set(resources, "TextControlForegroundDisabled", theme.Muted.Blend(theme.Background, 0.5d));

        Set(resources, "TextControlBorderBrush", theme.Border);
        Set(resources, "TextControlBorderBrushPointerOver", theme.Background.Blend(theme.Text, 0.28d));
        Set(resources, "TextControlBorderBrushFocused", theme.Primary);
        Set(resources, "TextControlBorderBrushDisabled", theme.Background.Blend(theme.Text, 0.08d));

        Set(resources, "TextControlPlaceholderForeground", theme.Muted);
        Set(resources, "TextControlPlaceholderForegroundPointerOver", theme.Muted);
        Set(resources, "TextControlPlaceholderForegroundFocused", theme.Muted);

        // The drop-downs sit next to those fields on the same tab, so they take
        // the same treatment; left alone, Fluent outlines them in a near-black
        // that reads as a different application's control.
        Set(resources, "ComboBoxBackground", rest);
        Set(resources, "ComboBoxBackgroundPointerOver", hover);
        Set(resources, "ComboBoxBackgroundPressed", hover);
        Set(resources, "ComboBoxBackgroundDisabled", theme.Background.Blend(theme.Text, 0.04d));

        Set(resources, "ComboBoxForeground", theme.Text);
        Set(resources, "ComboBoxForegroundFocused", theme.Text);
        Set(resources, "ComboBoxForegroundFocusedPressed", theme.Text);
        Set(resources, "ComboBoxForegroundDisabled", theme.Muted.Blend(theme.Background, 0.5d));

        Set(resources, "ComboBoxBorderBrush", theme.Border);
        Set(resources, "ComboBoxBorderBrushPointerOver", theme.Background.Blend(theme.Text, 0.28d));
        Set(resources, "ComboBoxBorderBrushPressed", theme.Background.Blend(theme.Text, 0.28d));
        Set(resources, "ComboBoxBorderBrushDisabled", theme.Background.Blend(theme.Text, 0.08d));

        static void Set(IResourceDictionary resources, string key, Rgb colour)
            => resources[key] = new SolidColorBrush(ToColor(colour, 1d));
    }

    /// <summary>
    /// Repaints Fluent's buttons in the theme's own colours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>Style Selector="Button"</c> setting <c>Background</c> does nothing
    /// visible: Fluent's button template paints its <c>ContentPresenter</c> from
    /// these named brushes, and the presenter's local value wins over anything
    /// set on the button. Overriding the brushes is the supported way in, and it
    /// reaches every button in the app rather than the ones a stylesheet
    /// remembered to name.
    /// </para>
    /// <para>
    /// The rest state is the card surface with a hairline, so a button reads as
    /// part of the panel; hover and pressed step toward the text colour rather
    /// than toward the accent, which keeps the accent meaning "this is the
    /// primary action" instead of "this is a button".
    /// </para>
    /// </remarks>
    private static void ApplyButtons(IResourceDictionary resources, Theme theme)
    {
        Rgb rest = theme.Surface;
        Rgb hover = theme.Background.Blend(theme.Text, theme.IsDark ? 0.16d : 0.10d);
        Rgb pressed = theme.Background.Blend(theme.Text, theme.IsDark ? 0.24d : 0.16d);

        Set(resources, "ButtonBackground", rest);
        Set(resources, "ButtonBackgroundPointerOver", hover);
        Set(resources, "ButtonBackgroundPressed", pressed);
        Set(resources, "ButtonBackgroundDisabled", theme.Background.Blend(theme.Text, 0.04d));

        Set(resources, "ButtonForeground", theme.Text);
        Set(resources, "ButtonForegroundPointerOver", theme.Text);
        Set(resources, "ButtonForegroundPressed", theme.Text);

        // Disabled text is the muted grey pulled halfway back to the background:
        // still readable, unmistakably inert. The Refresh button spends real time
        // in this state while a poll is in flight, so it has to look deliberate.
        Set(resources, "ButtonForegroundDisabled", theme.Muted.Blend(theme.Background, 0.5d));

        Set(resources, "ButtonBorderBrush", theme.Border);
        Set(resources, "ButtonBorderBrushPointerOver", theme.Background.Blend(theme.Text, 0.28d));
        Set(resources, "ButtonBorderBrushPressed", theme.Background.Blend(theme.Text, 0.28d));
        Set(resources, "ButtonBorderBrushDisabled", theme.Background.Blend(theme.Text, 0.08d));

        static void Set(IResourceDictionary resources, string key, Rgb colour)
            => resources[key] = new SolidColorBrush(ToColor(colour, 1d));
    }

    /// <summary>
    /// Feeds the primary colour to Fluent's accent slots.
    /// </summary>
    /// <remarks>
    /// Fluent expects a base accent plus three lighter and three darker steps, and
    /// uses them for hover, pressed and disabled states. Generating them by
    /// blending toward white and black keeps a hand-picked primary coherent
    /// through every control state without a theme author having to supply seven
    /// colours.
    /// </remarks>
    private static void ApplyAccent(IResourceDictionary resources, Theme theme)
    {
        Rgb accent = theme.Primary;
        var white = new Rgb(0xFF, 0xFF, 0xFF);
        var black = new Rgb(0x00, 0x00, 0x00);

        resources["SystemAccentColor"] = ToColor(accent, 1d);

        resources["SystemAccentColorLight1"] = ToColor(accent.Blend(white, 0.20d), 1d);
        resources["SystemAccentColorLight2"] = ToColor(accent.Blend(white, 0.40d), 1d);
        resources["SystemAccentColorLight3"] = ToColor(accent.Blend(white, 0.60d), 1d);

        resources["SystemAccentColorDark1"] = ToColor(accent.Blend(black, 0.20d), 1d);
        resources["SystemAccentColorDark2"] = ToColor(accent.Blend(black, 0.40d), 1d);
        resources["SystemAccentColorDark3"] = ToColor(accent.Blend(black, 0.60d), 1d);
    }

    private static void SetBrush(IResourceDictionary resources, string name, Rgb colour)
        => resources[Prefix + name] = new SolidColorBrush(ToColor(colour, 1d));

    private static Color ToColor(Rgb colour, double opacity)
        => Color.FromArgb(
            (byte)Math.Clamp(Math.Round(opacity * 255d), 0d, 255d),
            colour.R,
            colour.G,
            colour.B);
}
