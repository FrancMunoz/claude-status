using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClaudeStatus.App.Branding;
using ClaudeStatus.Usage;

namespace ClaudeStatus.App.Tray;

/// <summary>
/// Draws the macOS menu bar item's image: the mark, or three pulsing dots, with
/// the session count in a box beside it.
/// </summary>
/// <remarks>
/// <para>
/// Everything the Windows widget says with a control tree has to be drawn here,
/// because an <c>NSStatusItem</c> is one image and one run of plain text. A
/// rounded box cannot be written as text, and nothing in a status item animates,
/// so both the badge and the working dots live in the image.
/// </para>
/// <para>
/// <b>Template image.</b> The bytes are black on transparent and the platform
/// marks them <c>setTemplate:</c>, which throws the colour away and keeps the
/// alpha: macOS then paints the shape in the menu bar's own ink, light on a dark
/// bar and dark on a light one. That is why the digits are <i>cut out</i> of the
/// box rather than drawn on top of it - a white box with black digits would have
/// no edge at all on a light menu bar, where an ink box with the digits punched
/// through reads correctly in both appearances, like every system item.
/// </para>
/// <para>
/// Pure function plus a cache: the same inputs give back the same array, which is
/// what makes a 150 ms frame timer cost nothing but a call to <c>setImage:</c>.
/// </para>
/// </remarks>
public static class MenuBarImageRenderer
{
    /// <summary>Physical pixels per point.</summary>
    /// <remarks>
    /// The mark has always been rasterised at twice its point size so it is crisp
    /// on a HiDPI menu bar; every measurement below is in these pixels, and the
    /// platform divides by this to name the point size.
    /// </remarks>
    public const int Scale = 2;

    /// <summary>The item's height in points, matching the system's own icons.</summary>
    public const int Points = 16;

    /// <summary>The image's height in physical pixels.</summary>
    public const int Height = Points * Scale;

    /// <summary>How many frames one pulse of the dots is cut into.</summary>
    /// <remarks>
    /// Six at 150 ms is the 0.9 s cycle the widget animates continuously. Fewer
    /// reads as a blink rather than a wave; more costs another <c>setImage:</c> a
    /// second for motion nobody can see at 2 pt.
    /// </remarks>
    public const int FrameCount = 6;

    /// <summary>How long one frame is shown.</summary>
    public static TimeSpan FrameInterval { get; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How long the three dots take to come round again.</summary>
    public static TimeSpan Cycle { get; } = FrameInterval * FrameCount;

    /// <summary>The square the mark - or the dots standing in for it - is drawn in.</summary>
    private const int MarkBox = Height;

    /// <summary>
    /// How far the row's digits ride above the middle of the image.
    /// </summary>
    /// <remarks>
    /// One point, counted off the running item: the row's digits occupy rows 9-18
    /// of the bar where the image occupies 7-22, so their ink is centred a point
    /// higher than the image is. AppKit centres the image on the button and puts
    /// the title on the text baseline, and those two are not the same line - a
    /// font leaves descender room under digits that never use it.
    /// </remarks>
    private const int TextRise = 1 * Scale;

    /// <summary>
    /// The height of the badge's box.
    /// </summary>
    /// <remarks>
    /// Not quite the image's. The box is drawn from the top, and taking
    /// <see cref="TextRise"/> off each end is what moves its middle - and so the
    /// digits centred in it - onto the row's own optical centre. Drawn at the full
    /// 16 pt the digits were the right size and a pixel low, which is the one
    /// misalignment there is no hiding beside a line of text.
    /// </remarks>
    private const int BoxHeight = Height - (2 * TextRise);

    /// <summary>
    /// Between the mark and the badge, and between the badge and the row.
    /// </summary>
    /// <remarks>
    /// Five points, the same on both sides: the badge sits between two things it
    /// belongs equally to, and a count crowded against the mark while floating
    /// away from the numbers reads as part of the mark rather than as a reading of
    /// its own. Three was the first answer and read as tight.
    /// </remarks>
    private const int Gap = 5 * Scale;

    /// <summary>
    /// The blank the row's first glyph brings with it.
    /// </summary>
    /// <remarks>
    /// AppKit butts the title straight up against the image, so what separates the
    /// badge from the numbers is this bearing plus whatever the image draws after
    /// the box. Three points, counted off the running item - the row's leading "5"
    /// starts three pixels into its own advance. Subtracting it is what makes both
    /// gaps measure the same on the bar rather than only in this file; drawing the
    /// full <see cref="Gap"/> on both sides made the right one nearly twice the
    /// left. It is also why <c>MacOsStatusItem</c> no longer leads the title with
    /// an en space, which was six or seven points on top of the bearing again.
    /// </remarks>
    private const int TitleBearing = 3 * Scale;

    /// <summary>What the image draws after the box, to finish the gap off.</summary>
    private const int TrailingGap = Gap > TitleBearing ? Gap - TitleBearing : 0;

    /// <summary>
    /// The badge's corner.
    /// </summary>
    /// <remarks>
    /// The widget's 3 px and four more. At the widget's size a corner of a couple
    /// of pixels was all that separated "a box" from "a blot"; this box is two and
    /// a half times as tall, and the same radius on it read as a hard rectangle.
    /// Still well short of a pill, whose 16 would eat the room the digits need at
    /// the ends.
    /// </remarks>
    private const double Corner = 7d;

    /// <summary>Air between the digits' ink and the ends of the box.</summary>
    private const double SidePad = 2d;

    /// <summary>
    /// How tall the digits' ink is drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Twenty pixels at 2x is ten points, which is what the menu bar's own digits
    /// measure beside it - counted off a screenshot of the running item, not
    /// guessed: the row's "29%" and "58%" are ten pixels of ink on this display,
    /// and the badge is meant to read as part of the same line, not as a second,
    /// louder one. The padding that falls out of <see cref="BoxHeight"/> around
    /// them is 4 px above and below, rather than the widget's 3 in a badge only
    /// 13 px tall to begin with.
    /// </para>
    /// <para>
    /// Measured to the ink, not to the line box: a font's line carries ascender and
    /// descender room that digits never use, and fitting to it would leave the
    /// digits floating high in a box sized for letters that are not there.
    /// </para>
    /// </remarks>
    private const double DigitInk = 20d;

    /// <summary>The count the fixed box is sized for.</summary>
    /// <remarks>
    /// Two digits each side, and the widest pair of them. The box keeps this width
    /// whatever it is showing, so the item does not walk along the menu bar every
    /// time a turn starts or ends - <c>0/0</c> and <c>9/9</c> are the same width,
    /// centred, and only a third digit anywhere may push it wider.
    /// </remarks>
    private const string WidestFixedCount = "10/12";

    /// <summary>
    /// Diameter of one working dot.
    /// </summary>
    /// <remarks>
    /// Twice the widget's 4 px. The widget draws its dots into a 19 px box and
    /// this draws them into 32, so keeping the widget's number put a quarter of
    /// the ink in the same space and the sign read as a speck rather than as
    /// Claude thinking. Eight px of 32 is a shade more of the box than the
    /// widget's four of nineteen, which is the proportion that was right there.
    /// </remarks>
    private const double DotDiameter = 8d;

    /// <summary>
    /// Between two dots.
    /// </summary>
    /// <remarks>
    /// Not doubled with the dots: at 4 px the row of three would fill the mark's
    /// square edge to edge and run into the badge. Three leaves a pixel at each
    /// end and still reads as three dots rather than as a dash.
    /// </remarks>
    private const double DotGap = 3d;

    /// <summary>The opacity a dot rests at between its turns.</summary>
    private const double DotFloor = 0.3d;

    /// <summary>How far each dot lags the one before it, in seconds.</summary>
    private const double DotDelaySeconds = 0.3d;

    private static readonly Vector Dpi = new(96, 96);

    private static readonly RenderOptions Quality = new()
    {
        EdgeMode = EdgeMode.Antialias,
        BitmapInterpolationMode = BitmapInterpolationMode.HighQuality,
    };

    /// <summary>
    /// How the digits are rasterised.
    /// </summary>
    /// <remarks>
    /// The same reasoning as the tray icon's: this bitmap has an alpha channel and
    /// is re-tinted by the OS, so subpixel antialiasing would leave colour fringes
    /// that mean nothing, and hinting at 2x for a shape that is then halved would
    /// grid-fit to a size the image is never shown at.
    /// </remarks>
    private static readonly TextOptions GlyphOptions = new()
    {
        TextRenderingMode = TextRenderingMode.Antialias,
        TextHintingMode = TextHintingMode.None,
    };

    private static readonly Typeface BadgeFace = new(Typeface.Default.FontFamily, weight: FontWeight.Bold);

    /// <summary>
    /// Rasterised images by what they show.
    /// </summary>
    /// <remarks>
    /// The frame timer asks for the same six images over and over; rasterising them
    /// once is the difference between a swap and a render six times a second. The
    /// key holds the counts as well, because the badge beside the dots changes with
    /// them. Reference equality on the returned array is what lets the indicator
    /// skip a <c>setImage:</c> that would change nothing.
    /// </remarks>
    private static readonly ConcurrentDictionary<(int Frame, int Working, int Open, bool Watching), byte[]> Cache = new();

    /// <summary>
    /// The fitted font size, and the width the fixed box needs at it.
    /// </summary>
    /// <remarks>
    /// Both need a font, so neither can be a constant; both are the same for every
    /// image, so neither is worth measuring twice. <see cref="Lazy{T}"/> rather
    /// than a static field initialiser because the first measurement has to happen
    /// once Avalonia's font manager is up.
    /// </remarks>
    private static readonly Lazy<(double FontSize, double BoxWidth)> Metrics = new(Measure);

    /// <summary>
    /// The image for one state of the menu bar item.
    /// </summary>
    /// <param name="frame">
    /// Which frame of the working pulse to draw, or null for the mark. Null is not
    /// "frame zero": zero is the dots at their dimmest, which is still the dots.
    /// </param>
    /// <param name="working">Sessions with a turn in progress.</param>
    /// <param name="open">Sessions open at all - the badge's denominator.</param>
    /// <param name="watching">
    /// Whether the session watch is on. Off is not <c>0/0</c>: the badge goes away
    /// entirely and the image is the bare mark, as it was before any of this.
    /// </param>
    /// <returns>PNG bytes. The same inputs give back the same array.</returns>
    public static byte[] Render(int? frame, int working, int open, bool watching)
    {
        int slot = frame is { } index ? ((index % FrameCount) + FrameCount) % FrameCount : -1;
        working = Math.Max(working, 0);
        open = Math.Max(open, 0);

        // Nothing to say about sessions while the watch is off, so nothing to key
        // on either: every count collapses to the one bare mark.
        if (!watching)
        {
            (working, open) = (0, 0);
        }

        (int, int, int, bool) key = (slot, working, open, watching);
        if (Cache.TryGetValue(key, out byte[]? cached))
        {
            return cached;
        }

        // Six frames times the counts a machine actually reaches is a few dozen
        // images; a run that somehow walks past that starts again rather than
        // growing a cache nobody is reading from.
        if (Cache.Count > 256)
        {
            Cache.Clear();
        }

        byte[] rendered = Draw(frame is null ? null : slot, working, open, watching);
        return Cache.GetOrAdd(key, rendered);
    }

    /// <summary>
    /// How lit one dot is on one frame.
    /// </summary>
    /// <remarks>
    /// The widget's animation, sampled rather than reinterpreted: 0.3 to 1 and back
    /// over 0.9 s, sine eased at both ends, each dot 0.3 s behind the one before
    /// it. A sine ease in and out between a floor, a peak and the floor again is
    /// algebraically one raised cosine over the whole cycle - the two halves are
    /// the same expression - which is why this is one line and not a pair of cases.
    /// </remarks>
    internal static double DotOpacity(int frame, int dot)
    {
        double cycle = Cycle.TotalSeconds;
        double elapsed = (frame * FrameInterval.TotalSeconds) - (dot * DotDelaySeconds);

        // C#'s % keeps the sign of the left operand, and the delays make it
        // negative for the first frames of the second and third dots.
        double phase = ((elapsed % cycle) + cycle) % cycle;

        return DotFloor + ((1d - DotFloor) * (1d - Math.Cos(2d * Math.PI * phase / cycle)) / 2d);
    }

    private static byte[] Draw(int? frame, int working, int open, bool watching)
    {
        string count = IndicatorText.SessionCount(working, open);
        double badge = watching ? BadgeWidth(count) : 0d;
        int width = watching
            ? (int)Math.Ceiling(MarkBox + Gap + badge + TrailingGap)
            : MarkBox + TrailingGap;

        using var bitmap = new RenderTargetBitmap(new PixelSize(width, Height), Dpi);

        using (DrawingContext context = bitmap.CreateDrawingContext())
        using (context.PushRenderOptions(Quality))
        using (context.PushTextOptions(GlyphOptions))
        {
            if (frame is { } index)
            {
                DrawDots(context, index);
            }
            else
            {
                AppMark.Draw(context, new Rect(0, 0, MarkBox, MarkBox), MarkBox, Colors.Black);
            }

            if (watching)
            {
                DrawBadge(context, new Rect(MarkBox + Gap, 0, badge, BoxHeight), count);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    /// <summary>
    /// Three dots in the mark's square, each at its opacity for this frame.
    /// </summary>
    /// <remarks>
    /// Drawn dim rather than skipped. A template image keeps its alpha, so macOS
    /// tints a half-transparent dot to a half-transparent one - the fade survives
    /// the mask, which is the whole reason the pulse can be done this way at all.
    /// </remarks>
    private static void DrawDots(DrawingContext context, int frame)
    {
        double row = (3d * DotDiameter) + (2d * DotGap);
        double left = (MarkBox - row) / 2d;
        double centreY = MarkBox / 2d;
        double radius = DotDiameter / 2d;

        for (int dot = 0; dot < 3; dot++)
        {
            var brush = new SolidColorBrush(Colors.Black, DotOpacity(frame, dot));
            var centre = new Point(left + (dot * (DotDiameter + DotGap)) + radius, centreY);
            context.DrawEllipse(brush, null, centre, radius, radius);
        }
    }

    /// <summary>The box with the count punched through it.</summary>
    private static void DrawBadge(DrawingContext context, Rect box, string count)
    {
        var rectangle = new RectangleGeometry(box) { RadiusX = Corner, RadiusY = Corner };

        FormattedText digits = Build(count, Metrics.Value.FontSize);
        Rect ink = InkOf(digits);

        // Placed by the ink, so the padding above and below is the measured 3 px
        // rather than whatever the line box happens to leave. Centring the ink in a
        // box whose height is the ink plus twice the padding gives the same answer,
        // and keeps working when a taller glyph widens the ink.
        var origin = new Point(
            box.X + ((box.Width - ink.Width) / 2d) - ink.X,
            box.Y + ((box.Height - ink.Height) / 2d) - ink.Y);

        Geometry shape = digits.BuildGeometry(origin) is { } glyphs
            ? new CombinedGeometry(GeometryCombineMode.Exclude, rectangle, glyphs)
            : rectangle;

        context.DrawGeometry(Brushes.Black, null, shape);
    }

    /// <summary>How wide the box is for a count.</summary>
    private static double BadgeWidth(string count)
    {
        double fixedWidth = Metrics.Value.BoxWidth;
        double needed = InkOf(Build(count, Metrics.Value.FontSize)).Width + (2d * SidePad);
        return Math.Max(fixedWidth, Math.Ceiling(needed));
    }

    /// <summary>Fits the digits to the box once, and measures the fixed width.</summary>
    private static (double FontSize, double BoxWidth) Measure()
    {
        const double target = DigitInk;

        // Glyph outlines scale linearly with the font size, so one proportional
        // step lands almost exactly and a second cleans up the rounding. The
        // reference carries a slash because the slash is the tallest thing in the
        // count - fitting on digits alone would push it past the padding.
        double size = target * 1.4d;
        for (int pass = 0; pass < 3; pass++)
        {
            Rect ink = InkOf(Build("0/0", size));
            if (ink.Height <= 0d)
            {
                break;
            }

            size *= target / ink.Height;
        }

        double width = InkOf(Build(WidestFixedCount, size)).Width + (2d * SidePad);
        return (size, Math.Ceiling(width));
    }

    private static FormattedText Build(string text, double size)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            BadgeFace,
            size,
            Brushes.Black);

        // Tabular figures, as on the widget: proportional digits would shift the
        // count inside a box that is deliberately not moving.
        formatted.SetFontFeatures([FontFeature.Parse("+tnum")]);
        return formatted;
    }

    /// <summary>The bounds of the marks the text actually makes.</summary>
    private static Rect InkOf(FormattedText text)
        => text.BuildGeometry(default)?.Bounds ?? new Rect(0, 0, text.Width, text.Height);
}
