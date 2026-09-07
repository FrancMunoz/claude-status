using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClaudeStatus.Platform;
using ClaudeStatus.Theming;
using ClaudeStatus.Usage;

namespace ClaudeStatus.App.Tray;

/// <summary>
/// Draws the tray icon at runtime from a usage reading.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="RenderTargetBitmap.CreateDrawingContext()"/> rather than
/// <c>RenderTargetBitmap.Render(visual)</c>: the latter requires the visual to be
/// attached to a visible window, and this app deliberately has no window
/// (<c>PLAN.md</c> Phase 5). Drawing straight into the context needs neither.
/// </para>
/// <para>
/// Rendered at 2x the nominal size and left at 96 DPI. The tray downsamples it,
/// which keeps the glyph readable on a HiDPI display without us having to guess
/// the scale factor.
/// </para>
/// <para>
/// <b>Contrast is the whole problem here.</b> The first version drew near-white
/// ink unconditionally and vanished on the Windows light taskbar - legible only
/// once it turned red, so readable exactly when something was wrong and invisible
/// the rest of the time. The ink now follows <see cref="TrayBackground"/>, and
/// when that is unknown (Linux, where no portable signal exists) the glyph gets a
/// contrasting halo so it reads on any panel colour.
/// </para>
/// </remarks>
public static class TrayIconRenderer
{
    /// <summary>Nominal icon size in device-independent pixels.</summary>
    public const int NominalSize = 32;

    /// <summary>Supersampling factor.</summary>
    public const int Scale = 2;

    /// <summary>
    /// Extra glyph size, in nominal pixels, over the size the box alone suggests.
    /// </summary>
    /// <remarks>
    /// Only a starting guess now. <see cref="FitToInk"/> scales from here to
    /// whatever actually fills the box, so neither this nor <see cref="GlyphFraction"/>
    /// determines the final size - they just save the fit an iteration.
    /// </remarks>
    private const double GlyphBoost = 2d;

    /// <summary>Starting glyph size as a fraction of the icon box.</summary>
    private const double GlyphFraction = 0.72d;

    /// <summary>
    /// Opacity of a reading that is no longer current.
    /// </summary>
    /// <remarks>
    /// The value lives in <see cref="IndicatorText"/> now, shared with the macOS
    /// menu bar so the two fade alike. Much below it and the icon starts to read as
    /// "broken"; much above and the difference stops registering at 16 px, which is
    /// the only size that matters.
    /// </remarks>
    private const double StaleAlpha = IndicatorText.StaleAlpha;

    /// <summary>Clearance around the number, in nominal pixels.</summary>
    /// <remarks>
    /// Zero. The glyph is the icon, and every pixel of clearance is a pixel the
    /// number is not using; a tray icon that leaves a frame of empty space looks
    /// undersized beside the system's own, which run to their edges. Digits
    /// touching the edge look like digits.
    /// </remarks>
    private const double GlyphMargin = 0d;

    /// <summary>
    /// How far the glyph may be stretched vertically past its natural proportions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two digits need well under this to fill the box, so the cap does not touch
    /// the case it was all done for. It exists for the glyphs that are not
    /// numbers: <c>--</c> is a pair of dashes barely 4 px of ink tall, and filling
    /// 64 px with them turned each one into a solid block that read as a progress
    /// bar rather than as "no reading".
    /// </para>
    /// <para>
    /// So the rule is "spend the slack", not "fill the box at any cost" - anything
    /// short and wide keeps its shape.
    /// </para>
    /// </remarks>
    private const double MaxGlyphStretch = 1.6d;

    /// <summary>
    /// Height of the area the number is drawn into: the whole icon.
    /// </summary>
    /// <remarks>
    /// The icon used to give up its bottom strip to three 2 px progress bars and
    /// append a small <c>%</c> to the number. Both went on 2026-09-06: at 16 px
    /// neither was legible enough to mean anything, and every pixel they took was
    /// a pixel the number could not use. The number alone is the icon now; the
    /// three metrics live in the taskbar widget (<c>PLAN.md</c> Phase 8), for
    /// which this icon is the fallback.
    /// </remarks>
    internal static double GlyphBoxHeight => Size.Height;

    /// <summary>Antialiased edges for the ring and the stroked halo.</summary>
    private static readonly RenderOptions Quality = new()
    {
        EdgeMode = EdgeMode.Antialias,
        BitmapInterpolationMode = BitmapInterpolationMode.HighQuality,
    };

    /// <summary>
    /// How the glyph is rasterised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="TextRenderingMode.Antialias"/> is deliberate, not a default.</b>
    /// Left unspecified, Windows picks subpixel antialiasing, which tints glyph
    /// edges red and blue on the assumption they sit on a known opaque background.
    /// This icon has an alpha channel and sits on a taskbar whose colour we do not
    /// control, so those fringes are simply wrong. Grey antialiasing is what a
    /// transparent icon needs.
    /// </para>
    /// <para>
    /// Hinting is off for the same reason. Snapping stems to the pixel grid helps
    /// body text at its final size; here the glyph is drawn at 64 px and then
    /// downsampled by the tray to 16, 24 or 28, so grid-fitting at the wrong size
    /// distorts the shapes on the way to a size it never saw.
    /// </para>
    /// </remarks>
    private static readonly TextOptions GlyphOptions = new()
    {
        TextRenderingMode = TextRenderingMode.Antialias,
        TextHintingMode = TextHintingMode.None,
    };

    private static readonly PixelSize Size = new(NominalSize * Scale, NominalSize * Scale);
    private static readonly Vector Dpi = new(96, 96);

    /// <summary>Ink on a dark tray.</summary>
    private static readonly Color LightInk = Color.FromRgb(0xF2, 0xF2, 0xF2);

    /// <summary>Ink on a light tray.</summary>
    private static readonly Color DarkInk = Color.FromRgb(0x1A, 0x1A, 0x1A);

    /// <summary>Alert red for a dark tray.</summary>
    private static readonly Color AlertOnDark = Color.FromRgb(0xFF, 0x5D, 0x5F);

    /// <summary>
    /// Alert red for a light tray.
    /// </summary>
    /// <remarks>Darker, because the dark-tray red is washed out on a light panel.</remarks>
    private static readonly Color AlertOnLight = Color.FromRgb(0xC4, 0x18, 0x1B);

    /// <summary>Ink when there is no reading at all, on a dark tray.</summary>
    private static readonly Color UnknownOnDark = Color.FromRgb(0x9A, 0x9A, 0x9A);

    /// <summary>Ink when there is no reading at all, on a light tray.</summary>
    private static readonly Color UnknownOnLight = Color.FromRgb(0x5A, 0x5A, 0x5A);

    /// <summary>
    /// Renders an icon.
    /// </summary>
    /// <remarks>
    /// The caller owns the returned bitmap and must dispose the previous one -
    /// this runs on every poll, and leaking a bitmap a minute adds up over a day
    /// in the tray.
    /// </remarks>
    /// <param name="faded">
    /// Whether to draw the reading as out of date. Decided by the caller through
    /// <see cref="StalePolicy"/> rather than read from the snapshot here: the rule
    /// needs a clock and the configured poll interval, and this class deliberately
    /// knows about neither.
    /// </param>
    public static RenderTargetBitmap Render(
        UsageSnapshot? snapshot,
        IndicatorMode mode,
        ThresholdState state,
        IndicatorAlert alert = IndicatorAlert.None,
        TrayBackground background = TrayBackground.Unknown,
        bool faded = false)
    {
        UsageWindow? window = snapshot?.ForMode(mode);
        Palette palette = Palette.For(background, state);

        // An out-of-date reading is drawn faded. Everywhere else in the app says so
        // in words - the tooltip, the popup's banner, the report - but the icon is
        // the thing people actually glance at, and until now a three-hour-old 61 %
        // looked exactly like a live one. That is the reading someone acts on
        // without checking.
        if (faded)
        {
            palette = palette.Faded(StaleAlpha);
        }

        var bitmap = new RenderTargetBitmap(Size, Dpi);
        using (DrawingContext context = bitmap.CreateDrawingContext())
        using (context.PushRenderOptions(Quality))
        using (context.PushTextOptions(GlyphOptions))
        {
            if (alert == IndicatorAlert.NeedsCredential)
            {
                // Nothing useful can ever be shown until the user acts, so say so
                // rather than displaying a dash that looks like a transient glitch.
                DrawGlyph(
                    context,
                    "!",
                    Palette.For(background, ThresholdState.Exceeded),
                    Size.Width * 0.86,
                    Size.Height);
            }
            else if (snapshot is null && alert == IndicatorAlert.Unreachable)
            {
                // Never connected, and cannot. A dash here is indistinguishable
                // from "still loading", which is what it looked like for as long
                // as the endpoint stayed unreachable.
                DrawDisconnected(context, Palette.For(background, ThresholdState.Unknown));
            }
            else if (mode == IndicatorMode.Ring)
            {
                DrawRing(context, window, palette);
            }
            else
            {
                DrawPercentage(context, window, palette);
            }
        }

        return bitmap;
    }

    /// <summary>Draws the percentage as a bare number, which is what most people want at a glance.</summary>
    private static void DrawPercentage(DrawingContext context, UsageWindow? window, Palette palette)
    {
        // At the limit the number stops being the useful thing to show. "100" and
        // "99" differ by one glyph and mean quite different things - one is nearly
        // out, the other is out - so the exhausted state gets a shape rather than
        // a number, which is legible at 16 px in a way a third digit is not.
        if (window is not null && IsExhausted(window.Percent))
        {
            DrawExhausted(context, palette, Size.Height);
            return;
        }

        string text = window is null ? "--" : FormatPercent(window.Percent);

        // One requested size for every reading. There used to be a second, smaller
        // constant for "100"; it was a hand-tuned guess at the width three digits
        // need, and the fit now measures that exactly. Two digits - what is on
        // screen almost all the time - therefore grow as far as the box allows,
        // and three shrink by precisely as much as they must.
        DrawGlyph(
            context,
            text,
            palette,
            (Size.Width * GlyphFraction) + (GlyphBoost * Scale),
            Size.Height,

            // The "--" placeholder keeps its proportions. It is not a number, and
            // filling the box with two dashes makes them read as bars.
            stretchToFill: window is not null);
    }

    /// <summary>Whether a reading has consumed the whole window.</summary>
    /// <remarks>
    /// Delegates to <see cref="IndicatorText"/>, which is where the icon and the
    /// macOS menu bar item agree on what the readings mean.
    /// </remarks>
    internal static bool IsExhausted(double percent) => IndicatorText.IsExhausted(percent);

    /// <summary>
    /// A cross, for a window with nothing left in it.
    /// </summary>
    /// <remarks>
    /// Drawn rather than typed. A font's "×" is designed to sit beside lowercase
    /// text and comes out thin and small next to the bold digits it replaces;
    /// strokes give exact control of weight, which is the whole difference between
    /// legible and smudged once the tray has downsampled this to 16 px.
    /// </remarks>
    private static void DrawExhausted(DrawingContext context, Palette palette, double boxHeight)
    {
        double side = Math.Min(Size.Width, boxHeight) * 0.62d;
        double centreX = Size.Width / 2d;
        double centreY = boxHeight / 2d;
        double arm = side / 2d;

        var pen = new Pen(new SolidColorBrush(palette.Ink), side * 0.26d)
        {
            LineCap = PenLineCap.Round,
        };

        if (palette.Halo is { } halo)
        {
            var haloPen = new Pen(new SolidColorBrush(halo), (side * 0.26d) + HaloThickness)
            {
                LineCap = PenLineCap.Round,
            };

            DrawCross(context, haloPen, centreX, centreY, arm);
        }

        DrawCross(context, pen, centreX, centreY, arm);
    }

    private static void DrawCross(DrawingContext context, Pen pen, double x, double y, double arm)
    {
        context.DrawLine(pen, new Point(x - arm, y - arm), new Point(x + arm, y + arm));
        context.DrawLine(pen, new Point(x + arm, y - arm), new Point(x - arm, y + arm));
    }

    /// <summary>
    /// A struck-through circle, for "never reached the endpoint".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct in shape from both the credential warning and the exhausted cross,
    /// because all three can only be told apart by silhouette at this size - colour
    /// alone does not survive a monochrome menu-bar mask on macOS.
    /// </para>
    /// <para>
    /// Drawn in the "unknown" grey rather than red on purpose. A network that is
    /// down is not something the user did or can fix from here, and the app has
    /// been careful throughout not to raise an alarm about it.
    /// </para>
    /// </remarks>
    private static void DrawDisconnected(DrawingContext context, Palette palette)
    {
        double radius = Math.Min(Size.Width, Size.Height) * 0.30d;
        var centre = new Point(Size.Width / 2d, Size.Height / 2d);
        double thickness = radius * 0.34d;
        double slash = radius * 0.72d;

        void Stroke(Color colour, double extra)
        {
            var pen = new Pen(new SolidColorBrush(colour), thickness + extra)
            {
                LineCap = PenLineCap.Round,
            };

            context.DrawEllipse(brush: null, pen, centre, radius, radius);
            context.DrawLine(
                pen,
                new Point(centre.X - slash, centre.Y - slash),
                new Point(centre.X + slash, centre.Y + slash));
        }

        if (palette.Halo is { } halo)
        {
            Stroke(halo, HaloThickness);
        }

        Stroke(palette.Ink, 0d);
    }

    /// <summary>
    /// Draws centred bold text, with a halo when the background is unknown.
    /// </summary>
    /// <remarks>
    /// The halo is the same glyph drawn underneath at eight offsets. Crude, but it
    /// needs no geometry conversion and gives a clean edge at 16 px, which a blur
    /// does not.
    /// </remarks>
    /// <param name="boxHeight">Vertical space the glyph may use, from the top.</param>
    private static void DrawGlyph(
        DrawingContext context,
        string text,
        Palette palette,
        double fontSize,
        double boxHeight,
        bool stretchToFill = true)
    {
        var typeface = new Typeface(Typeface.Default.FontFamily, weight: FontWeight.Bold);

        FormattedText Build(string content, double size) => new(
            content,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            new SolidColorBrush(palette.Ink));

        // A halo is stroked outside the glyph, so it needs room of its own.
        double halo = palette.Halo is null ? 0d : HaloThickness;
        double margin = GlyphMargin * Scale;
        double maxWidth = Size.Width - halo - (margin * 2);
        double maxHeight = boxHeight - halo - (margin * 2);

        // Sized from the ink, not from the line box.
        //
        // FormattedText.Height is the whole typographic line - ascender, descender
        // and leading - and digits use only the cap height in the middle of it.
        // Fitting to that left roughly a third of the icon as invisible padding
        // and made the glyph look small beside the system's own tray icons, which
        // fill their box. Measuring the geometry's bounds instead means the digits
        // themselves fill the space.
        double fitted = FitToInk(fontSize, maxWidth, maxHeight, Measure);

        (Rect ink, FormattedText main) = Measure(fitted);

        // How much taller the digits can be drawn than the uniform fit allows.
        //
        // FitToInk takes the SMALLER of the width and height ratios, so whichever
        // axis is tighter decides the size and the other is left with slack. For
        // two digits that axis is width, which left a band of empty pixels above
        // and below the number in every icon the app has ever drawn.
        //
        // Scaling the y axis separately spends it. The digits stop being
        // proportional, which is the trade: a tray icon is read at 16 px in the
        // corner of a screen, where height is legibility and correct letterforms
        // are a detail nobody can resolve anyway.
        double stretch = stretchToFill && ink.Height > 0d
            ? Math.Clamp(Math.Max(1d, maxHeight) / ink.Height, 1d, MaxGlyphStretch)
            : 1d;

        // Placed by ink bounds too: subtracting the ink's own offset is what turns
        // "draw the line box here" into "put the digits exactly here".
        double left = margin + Math.Max(0d, (maxWidth - ink.Width) / 2) - ink.X;

        // Pinned to the top of the box rather than centred in it, because the
        // stretch below expands downwards from that edge and fills the rest.
        double top = margin - ink.Y;

        var origin = new Point(left, top);

        // Scaled about the top of the glyph box, so the ink starts where it was
        // placed and grows down to fill the rest.
        using IDisposable stretched = context.PushTransform(
            Matrix.CreateTranslation(0d, -margin)
            * Matrix.CreateScale(1d, stretch)
            * Matrix.CreateTranslation(0d, margin));

        (Rect Ink, FormattedText Main) Measure(double size)
        {
            FormattedText number = Build(text, size);
            return (InkOf(number, new Point(0, 0)), number);
        }

        if (palette.Halo is { } haloColour)
        {
            var pen = new Pen(new SolidColorBrush(haloColour), HaloThickness)
            {
                LineJoin = PenLineJoin.Round,
                LineCap = PenLineCap.Round,
            };

            // A stroked glyph outline, not the same text stamped at eight offsets:
            // the offset trick smears badly once the glyph is downsampled to 16 px,
            // producing a doubled, illegible mark. Stroking the real geometry keeps
            // the letterforms intact.
            if (main.BuildGeometry(origin) is { } outline)
            {
                context.DrawGeometry(brush: null, pen, outline);
            }
        }

        context.DrawText(main, origin);
    }

    /// <summary>
    /// The bounds of the marks a run of text actually makes.
    /// </summary>
    /// <remarks>
    /// Falls back to the line box when a face reports no geometry, which happens
    /// for whitespace and could happen for a glyph the font does not have.
    /// </remarks>
    private static Rect InkOf(FormattedText text, Point origin)
        => text.BuildGeometry(origin)?.Bounds
        ?? new Rect(origin.X, origin.Y, text.Width, text.Height);

    /// <summary>
    /// Finds the font size whose ink fills the box.
    /// </summary>
    /// <remarks>
    /// Glyph outlines scale linearly with font size, so one proportional step
    /// lands almost exactly on the answer and a second cleans up the rounding
    /// that hinting and the descender-free digits introduce. That is far better
    /// than stepping down 4 % at a time, which could only ever shrink and so
    /// could never recover the space the line-box measurement was wasting.
    /// </remarks>
    private static double FitToInk(
        double requested,
        double maxWidth,
        double maxHeight,
        Func<double, (Rect Ink, FormattedText Main)> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);

        if (maxWidth <= 0d || maxHeight <= 0d)
        {
            return requested;
        }

        double size = requested;
        for (int pass = 0; pass < 3; pass++)
        {
            (Rect ink, _) = measure(size);
            if (ink.Width <= 0d || ink.Height <= 0d)
            {
                return size;
            }

            double factor = Math.Min(maxWidth / ink.Width, maxHeight / ink.Height);

            // A hair under, so rounding in the rasteriser cannot push a glyph one
            // pixel past the edge it was measured to touch exactly.
            size *= factor * 0.995d;
        }

        return size;
    }

    /// <summary>Width of the halo stroke around the glyph.</summary>
    private static double HaloThickness => Size.Width * 0.075;

    /// <summary>Draws a filled arc, for people who prefer a shape to a number.</summary>
    private static void DrawRing(DrawingContext context, UsageWindow? window, Palette palette)
    {
        double thickness = Size.Width * 0.16;
        double radius = (Size.Width / 2.0) - (thickness / 2.0) - 1;
        var centre = new Point(Size.Width / 2.0, Size.Height / 2.0);

        // The full track first, so an empty ring still reads as "a ring at zero"
        // rather than as a missing icon. Its colour follows the ink, so it stays
        // visible whichever way round the tray is painted.
        context.DrawEllipse(
            brush: null,
            new Pen(new SolidColorBrush(palette.Track), thickness),
            centre,
            radius,
            radius);

        if (window is null || window.Percent <= 0)
        {
            return;
        }

        double percent = Math.Clamp(window.Percent, 0d, 100d);

        // A full sweep has to be drawn as an ellipse, not an arc. ArcTo from a
        // point back to itself is degenerate and renders as a dot - so at 100 %,
        // the moment the ring matters most, it silently collapsed to a speck.
        // Caught by looking at the rendered icon; the test only asserted that a
        // bitmap came back non-null.
        if (percent >= 99.95)
        {
            if (palette.Halo is { } fullHalo)
            {
                context.DrawEllipse(
                    brush: null,
                    new Pen(new SolidColorBrush(fullHalo), thickness * 1.45),
                    centre,
                    radius,
                    radius);
            }

            context.DrawEllipse(
                brush: null,
                new Pen(new SolidColorBrush(palette.Ink), thickness),
                centre,
                radius,
                radius);
            return;
        }

        double sweep = percent / 100d * 2 * Math.PI;

        // Start at twelve o'clock and go clockwise, which is how a progress ring
        // is read everywhere else.
        var geometry = new StreamGeometry();
        using (StreamGeometryContext geometryContext = geometry.Open())
        {
            var start = new Point(centre.X, centre.Y - radius);
            geometryContext.BeginFigure(start, isFilled: false);
            geometryContext.ArcTo(
                new Point(
                    centre.X + (radius * Math.Sin(sweep)),
                    centre.Y - (radius * Math.Cos(sweep))),
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: sweep > Math.PI,
                sweepDirection: SweepDirection.Clockwise);
            geometryContext.EndFigure(isClosed: false);
        }

        if (palette.Halo is { } halo)
        {
            context.DrawGeometry(
                brush: null,
                new Pen(new SolidColorBrush(halo), thickness * 1.45, lineCap: PenLineCap.Round),
                geometry);
        }

        context.DrawGeometry(
            brush: null,
            new Pen(new SolidColorBrush(palette.Ink), thickness, lineCap: PenLineCap.Round),
            geometry);
    }

    /// <summary>
    /// Formats a percentage as a whole number for the icon and tooltip.
    /// </summary>
    /// <remarks>
    /// "100" is three characters and simply renders at a smaller size rather than
    /// being abbreviated. The rounding itself lives in <see cref="IndicatorText"/>,
    /// shared with the macOS menu bar item.
    /// </remarks>
    internal static string FormatPercent(double percent) => IndicatorText.FormatPercent(percent);

    /// <summary>The colours for one combination of tray background and threshold state.</summary>
    /// <param name="Ink">The glyph colour.</param>
    /// <param name="Track">The ring's unfilled track.</param>
    /// <param name="Halo">An outline colour, or null when the background is known.</param>
    internal readonly record struct Palette(Color Ink, Color Track, Color? Halo)
    {
        /// <summary>Picks colours that contrast with the given background.</summary>
        /// <remarks>
        /// An unknown background gets light ink plus a dark halo - the subtitle
        /// trick. White-on-dark looks native, and white-with-an-outline stays
        /// readable on light. It is the only choice that cannot vanish entirely.
        /// </remarks>
        public static Palette For(TrayBackground background, ThresholdState state)
        {
            bool onLight = background == TrayBackground.Light;

            Color ink = state switch
            {
                ThresholdState.Exceeded => onLight ? AlertOnLight : AlertOnDark,
                ThresholdState.Unknown => onLight ? UnknownOnLight : UnknownOnDark,
                _ => onLight ? DarkInk : LightInk,
            };

            // A mid grey is the only track that survives both panels when we do
            // not know which one we are on: white vanishes on light, black on dark.
            Color track = background switch
            {
                TrayBackground.Light => Color.FromArgb(0x3A, 0x00, 0x00, 0x00),
                TrayBackground.Dark => Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF),
                _ => Color.FromArgb(0x70, 0x88, 0x88, 0x88),
            };

            Color? halo = background == TrayBackground.Unknown
                ? Color.FromArgb(0xD0, 0x0E, 0x0E, 0x0E)
                : null;

            return new Palette(ink, track, halo);
        }

        /// <summary>
        /// The same colours, faded, for a reading that is no longer current.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Alpha only - the hue is left alone. Recolouring a stale reading to grey
        /// would throw away the one thing worth keeping: a stale 95 % is still the
        /// best evidence available that the user is near their limit, and it should
        /// still be red. Fading says "this is old" without also saying "and I have
        /// forgotten what it was".
        /// </para>
        /// <para>
        /// Reducing alpha rather than blending toward a background colour is what a
        /// transparent icon wants: the taskbar shows through by exactly the amount
        /// taken away, whatever colour that taskbar happens to be. It also works
        /// with the macOS template-icon mask, which is read from alpha.
        /// </para>
        /// </remarks>
        public Palette Faded(double alpha) => new(
            Fade(Ink, alpha),
            Fade(Track, alpha),
            Halo is { } halo ? Fade(halo, alpha) : null);

        /// <summary>Scales a colour's existing alpha, preserving its hue.</summary>
        /// <remarks>
        /// Scaling rather than assigning matters for the ring's track, which is
        /// already semi-transparent: assigning would make a faded track more solid
        /// than a fresh one.
        /// </remarks>
        private static Color Fade(Color colour, double alpha) => Color.FromArgb(
            (byte)Math.Clamp(Math.Round(colour.A * Math.Clamp(alpha, 0d, 1d)), 0d, 255d),
            colour.R,
            colour.G,
            colour.B);
    }
}
