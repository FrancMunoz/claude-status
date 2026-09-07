using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ClaudeStatus.App.Branding;

/// <summary>
/// The application mark, as one path.
/// </summary>
/// <remarks>
/// <para>
/// Taken verbatim from <c>Assets/app-mark.svg</c>, which is kept beside this
/// file as the provenance of the coordinates - they are not the sort of thing
/// anyone should try to read or edit by hand.
/// </para>
/// <para>
/// A path rather than a bitmap because it is drawn at three very different sizes:
/// 12 device-independent pixels beside a window heading, and 32 physical pixels
/// for a menu bar item on a HiDPI display. One outline scales to all of them; a
/// PNG would need a set of files and would still be wrong on the next display.
/// </para>
/// <para>
/// The drawing is a gauge - a thick ring left open at the bottom, with two bars
/// in the hole - and it is made of holes rather than strokes on purpose: every
/// consumer fills it with a single brush, so anything that had to read as a
/// second colour has to be a gap instead.
/// </para>
/// <para>
/// The ring's inner arc runs opposite to its outer one and the bars run the same
/// way as the outer, so the figure comes out identically under either fill rule.
/// That is why no <c>F0</c>/<c>F1</c> prefix appears below: whatever the parser
/// defaults to cannot change what this draws.
/// </para>
/// </remarks>
public static class AppMark
{
    /// <summary>The mark's outline, in SVG path syntax.</summary>
    public const string PathData =
        "M 31.5 76.579 A 33 33 0 1 1 64.5 76.579 L 56.5 62.722 A 17 17 0 1 0 39.5 62.722 Z M 36 40 H 60 V 46 H 36 Z M 40 50 H 56 V 56 H 40 Z";

    /// <summary>The mark as geometry, parsed once.</summary>
    /// <remarks>
    /// Bound from AXAML with <c>{x:Static}</c> so the window heading and the menu
    /// bar item cannot drift apart: there is one outline and both draw it.
    /// </remarks>
    public static Geometry Geometry { get; } = Geometry.Parse(PathData);

    /// <summary>
    /// Renders the mark as an opaque PNG, for a platform that wants image bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Black on transparent, and the colour is deliberately not a parameter. This
    /// feeds a macOS template image, which throws the colours away and keeps only
    /// the alpha - the menu bar then tints the result to match its own text, in
    /// dark mode and light, which is the one way to be sure the mark matches the
    /// number beside it.
    /// </para>
    /// <para>
    /// Rendered at the physical pixel size the caller asks for, so a HiDPI menu bar
    /// gets a crisp mark rather than a scaled-up small one.
    /// </para>
    /// </remarks>
    /// <param name="pixels">The square size to render, in physical pixels.</param>
    public static byte[] ToPng(int pixels) => ToPng(pixels, Colors.Black, inset: 0d);

    /// <summary>
    /// Renders the mark as a PNG in a given colour, inset from the edges.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The application icon, as opposed to the menu bar's template image. That one
    /// must stay black (see the overload above); this one is drawn in the brand
    /// colour and is never re-tinted by anything.
    /// </para>
    /// <para>
    /// <paramref name="inset"/> exists because an icon is not a glyph in a text
    /// run: every platform draws it inside a grid cell it does not tell us about,
    /// and a mark rendered edge to edge looks larger than its neighbours and gets
    /// clipped by rounded masks. A small margin is what makes it sit in a Dock or
    /// a taskbar as though it belongs there.
    /// </para>
    /// </remarks>
    /// <param name="pixels">The square size to render, in physical pixels.</param>
    /// <param name="colour">The fill.</param>
    /// <param name="inset">Fraction of the canvas to leave clear on each side, 0 to 0.4.</param>
    public static byte[] ToPng(int pixels, Color colour, double inset)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(inset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inset, 0.4d);

        var size = new PixelSize(pixels, pixels);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));

        using (DrawingContext context = bitmap.CreateDrawingContext())
        using (context.PushRenderOptions(new RenderOptions { EdgeMode = EdgeMode.Antialias }))
        {
            Rect bounds = Geometry.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return [];
            }

            // The box the mark is fitted into, once the margin is taken off both
            // sides. At inset 0 this is the whole canvas and the maths below is
            // exactly what it always was.
            double box = pixels * (1d - (2d * inset));

            // Uniform, and centred on whichever axis has room left over: the mark is
            // not square, and stretching it to fill a square box would distort it.
            double scale = Math.Min(box / bounds.Width, box / bounds.Height);

            using (context.PushTransform(
                Matrix.CreateTranslation(-bounds.X, -bounds.Y)
                * Matrix.CreateScale(scale, scale)
                * Matrix.CreateTranslation(
                    (pixels - (bounds.Width * scale)) / 2,
                    (pixels - (bounds.Height * scale)) / 2)))
            {
                context.DrawGeometry(new SolidColorBrush(colour), null, Geometry);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }
}
