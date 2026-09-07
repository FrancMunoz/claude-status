namespace ClaudeStatus.Theming;

/// <summary>
/// A colour scheme for the app's windows.
/// </summary>
/// <remarks>
/// <para>
/// A theme declares four colours and nothing else. Everything else - the muted
/// grey for captions, the card background, the hairline border - is derived from
/// those four, so a theme author cannot produce a combination that is internally
/// inconsistent. Asking for a grey separately would invite exactly that: a grey
/// picked against the wrong background and invisible on the real one.
/// </para>
/// <para>
/// The tray icon is deliberately <b>not</b> themed. It sits on a taskbar this app
/// does not control, and a themed glyph would be invisible on half of them - the
/// bug fixed in <c>92acf84</c>. It keeps following the taskbar's own light or
/// dark appearance.
/// </para>
/// </remarks>
/// <param name="Id">Stable key stored in settings, e.g. <c>nebula</c>.</param>
/// <param name="Name">Display name. A proper noun, so it is not translated.</param>
/// <param name="Primary">Highlights, headings, accents, and the OS accent colour for controls.</param>
/// <param name="Background">The window background.</param>
/// <param name="Text">Body text.</param>
/// <param name="Alert">Over-threshold bars and warnings, tuned to sit on this background.</param>
public sealed record Theme(
    string Id,
    string Name,
    Rgb Primary,
    Rgb Background,
    Rgb Text,
    Rgb Alert)
{
    /// <summary>
    /// Whether this is a dark theme.
    /// </summary>
    /// <remarks>
    /// Derived from the background rather than declared, so it cannot disagree
    /// with the colours. It selects the light or dark variant of the built-in
    /// control theme, which is what makes buttons and sliders match.
    /// </remarks>
    public bool IsDark => Background.IsDark;

    /// <summary>
    /// The grey for captions and secondary labels.
    /// </summary>
    /// <remarks>
    /// The text colour pulled 45 % of the way toward the background - which is
    /// what "extract the grey from the text colour" means in practice. It stays
    /// legible on any background by construction, and it tints: a warm theme gets
    /// a warm grey rather than a neutral one dropped on top.
    /// </remarks>
    public Rgb Muted => Text.Blend(Background, 0.45d);

    /// <summary>Background for cards, notices and the report's rows.</summary>
    /// <remarks>A hint of the text colour, so it reads as raised on dark and inset on light.</remarks>
    public Rgb Surface => Background.Blend(Text, IsDark ? 0.08d : 0.05d);

    /// <summary>Hairline borders and separators.</summary>
    public Rgb Border => Background.Blend(Text, IsDark ? 0.18d : 0.14d);

    /// <summary>The groove behind a usage bar.</summary>
    /// <remarks>
    /// Deliberately a shade stronger than <see cref="Border"/>. A hairline is
    /// meant to be noticed only when looked for; a track has to read as the
    /// bar's full extent at a glance, or a low percentage looks like a stray
    /// mark rather than a small reading.
    /// </remarks>
    public Rgb Track => Background.Blend(Text, IsDark ? 0.22d : 0.16d);

    /// <summary>
    /// Text and icons drawn <em>on top of</em> <see cref="Primary"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Black or white, whichever actually measures better against this primary -
    /// not the theme's own text colour, which is chosen against the background
    /// and would vanish on a saturated accent. This is what lets a filled accent
    /// button exist in every theme without each one declaring a fifth colour.
    /// </para>
    /// <para>
    /// Choosing by <see cref="Rgb.IsDark"/> instead looks equivalent and is not.
    /// Every primary here has to contrast with both a light and a dark
    /// background, which forces them all into the middle of the range - and in
    /// that middle, "is it dark?" and "which of black and white reads better on
    /// it?" disagree for eight of the eleven built-ins. Only the measurement is
    /// right.
    /// </para>
    /// <para>
    /// A theme supplied in a file could still name a primary that neither black
    /// nor white clears 4.5:1 on. The built-in set is asserted; a dropped-in file
    /// gets the better of the two and no promise.
    /// </para>
    /// </remarks>
    public Rgb OnPrimary
    {
        get
        {
            var white = new Rgb(0xFF, 0xFF, 0xFF);
            var black = new Rgb(0x00, 0x00, 0x00);

            return black.ContrastWith(Primary) >= white.ContrastWith(Primary) ? black : white;
        }
    }

    /// <summary>Background for the warning banner, from the alert colour.</summary>
    public Rgb AlertSurface => Background.Blend(Alert, 0.16d);

    /// <summary>Border for the warning banner.</summary>
    public Rgb AlertBorder => Background.Blend(Alert, 0.45d);

    /// <summary>Contrast ratio of body text against the background.</summary>
    /// <remarks>WCAG asks for 4.5 on body text. Asserted for every built-in theme.</remarks>
    public double TextContrast => Text.ContrastWith(Background);

    /// <summary>Contrast ratio of the muted grey against the background.</summary>
    public double MutedContrast => Muted.ContrastWith(Background);
}
