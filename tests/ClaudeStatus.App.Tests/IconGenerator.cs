using System.Buffers.Binary;
using Avalonia;
using Avalonia.Media;
using ClaudeStatus.App.Branding;
using ClaudeStatus.Theming;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// Rebuilds the application icons from <see cref="ClaudeMark"/>.
/// </summary>
/// <remarks>
/// <para>
/// The icons are committed binaries, so this does not run in CI. It runs when
/// somebody asks for it:
/// </para>
/// <code>
/// $env:CLAUDESTATUS_WRITE_ICONS = 'src/ClaudeStatus.App/Assets'
/// dotnet test tests/ClaudeStatus.App.Tests --filter-method '*Regenerates*'
/// </code>
/// <para>
/// It lives here rather than in a build script because the only correct renderer
/// for this outline is the one the app itself uses. <c>claude-mark.svg</c> cannot
/// be handed to a general-purpose converter: its 96-unit viewBox contains a group
/// translated by 19.2 with no matching scale, so the ink sits in roughly the
/// top-left quarter of the canvas. Rendering it verbatim is exactly how the
/// previous <c>claude-mark.icns</c> came to be a small mark in the corner of an
/// otherwise empty square. <see cref="ClaudeMark.ToPng(int, Color, double)"/>
/// fits the path's real bounds instead, which is what every view already does
/// with <c>Stretch.Uniform</c>.
/// </para>
/// <para>
/// The other assertions in this class do run in CI, and they are the ones that
/// would have caught that icns: they check the shipped icons contain the sizes
/// they claim and that the mark actually covers the canvas.
/// </para>
/// </remarks>
[Collection(HeadlessTests.Name)]
public class IconGenerator
{
    /// <summary>The brand coral, from the Claude theme. The icon is not themed; this is the mark's own colour.</summary>
    private static Color Coral
    {
        get
        {
            Rgb primary = ThemeCatalog.BuiltIn.Single(t => t.Id == "claude").Primary;
            return Color.FromRgb(primary.R, primary.G, primary.B);
        }
    }

    /// <summary>
    /// A margin, because an icon is not a glyph: platforms draw it in a cell they
    /// do not describe, and edge-to-edge ink looks oversized beside its neighbours.
    /// </summary>
    private const double Inset = 0.10d;

    /// <summary>Sizes Windows actually asks for, smallest first.</summary>
    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>
    /// The icns types, as (OSType, pixels).
    /// </summary>
    /// <remarks>
    /// Each logical size is listed twice under different types - ic07/ic11 and so
    /// on - because macOS picks by type, not by inspecting the image, and a
    /// missing type is a size it will scale badly from another rather than an
    /// error anyone sees.
    /// </remarks>
    private static readonly (string Type, int Pixels)[] IcnsEntries =
    [
        ("icp4", 16), ("icp5", 32), ("ic11", 32), ("icp6", 64), ("ic12", 64),
        ("ic07", 128), ("ic08", 256), ("ic13", 256), ("ic09", 512), ("ic14", 512),
        ("ic10", 1024),
    ];

    private static string? OutputDirectory
        => Environment.GetEnvironmentVariable("CLAUDESTATUS_WRITE_ICONS");

    private static string AssetsDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClaudeStatus.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.SkipWhen(directory is null, "Running outside the repository; assets unavailable.");
        return Path.Combine(directory!.FullName, "src", "ClaudeStatus.App", "Assets");
    }

    [Fact]
    public void Regenerates_the_application_icons()
    {
        string? target = OutputDirectory;
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(target),
            "Set CLAUDESTATUS_WRITE_ICONS to the assets directory to rewrite the icons.");

        HeadlessAppFixture.Invoke(() =>
        {
            Directory.CreateDirectory(target!);
            File.WriteAllBytes(Path.Combine(target!, "claude-mark.ico"), BuildIco());
            File.WriteAllBytes(Path.Combine(target!, "claude-mark.icns"), BuildIcns());
        });
    }

    [Fact]
    public void The_shipped_ico_carries_every_size_Windows_asks_for()
    {
        byte[] ico = File.ReadAllBytes(Path.Combine(AssetsDirectory(), "claude-mark.ico"));

        ico.Length.Should().BeGreaterThan(6, "an icon with no directory is not an icon");
        BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)).Should().Be(1, "type 1 is an icon, 2 is a cursor");

        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        count.Should().Be(IcoSizes.Length);

        // 0 in the width byte means 256; anything else is the literal size.
        int[] declared = Enumerable.Range(0, count)
            .Select(i => ico[6 + (i * 16)] == 0 ? 256 : ico[6 + (i * 16)])
            .ToArray();

        declared.Should().BeEquivalentTo(IcoSizes);
    }

    [Fact]
    public void The_shipped_icns_carries_every_type_macOS_looks_for()
    {
        byte[] icns = File.ReadAllBytes(Path.Combine(AssetsDirectory(), "claude-mark.icns"));

        System.Text.Encoding.ASCII.GetString(icns, 0, 4).Should().Be("icns");

        var types = new List<string>();
        int offset = 8;
        while (offset + 8 <= icns.Length)
        {
            string type = System.Text.Encoding.ASCII.GetString(icns, offset, 4);
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(icns.AsSpan(offset + 4));
            if (length < 8 || offset + length > icns.Length)
            {
                break;
            }

            types.Add(type);
            offset += length;
        }

        types.Should().BeEquivalentTo(IcnsEntries.Select(e => e.Type));
    }

    [Fact]
    public void The_mark_fills_the_icon_rather_than_sitting_in_a_corner()
    {
        // The regression that shipped: claude-mark.icns was a small mark in the
        // top-left of an empty square, because the SVG was rendered verbatim and
        // its ink occupies about a quarter of its own viewBox. Measuring the ink
        // is the only check that would have failed on it - the file was a valid
        // icns of the right dimensions the whole time.
        HeadlessAppFixture.Invoke(() =>
        {
            byte[] png = ClaudeMark.ToPng(256, Coral, Inset);
            Rect ink = InkBounds(png, 256);

            // Centred: the margins on opposite sides match.
            (ink.Left - (256 - ink.Right)).Should().BeInRange(-2, 2, "horizontally centred");
            (ink.Top - (256 - ink.Bottom)).Should().BeInRange(-2, 2, "vertically centred");

            // And big: the longer axis fills what the inset leaves. The band is
            // for antialiasing, which spreads ink about a pixel past the exact
            // geometric edge on each side - the point of this assertion is the
            // difference between "fills the canvas" and "a quarter of it", not
            // sub-pixel accuracy.
            double expected = 256 * (1d - (2d * Inset));
            Math.Max(ink.Width, ink.Height).Should().BeInRange(expected - 3, expected + 3);
        });
    }

    [Fact]
    public void The_menu_bar_template_icon_is_still_black_and_fills_its_canvas()
    {
        // ToPng(int) feeds the macOS NSStatusItem as a *template* image: the
        // system throws the colours away, keeps the alpha, and tints the result
        // to match the menu bar's own text. Black is therefore not a style choice
        // there, and an inset would shrink the mark against the numbers beside
        // it - so the colour and inset this overload passes are load-bearing.
        //
        // It routes through the parameterised overload added for the app icons,
        // and the only test that covered it asserted that some PNG came out.
        HeadlessAppFixture.Invoke(() =>
        {
            byte[] png = ClaudeMark.ToPng(64);
            byte[] pixels = Pixels(png, 64);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] > 8)
                {
                    (pixels[i] + pixels[i + 1] + pixels[i + 2])
                        .Should().Be(0, "a template image must be black, not the brand colour");
                }
            }

            Rect ink = InkBounds(png, 64);
            Math.Max(ink.Width, ink.Height)
                .Should().BeInRange(61, 65, "the template icon takes no inset");
        });
    }

    /// <summary>Decodes a PNG to raw BGRA bytes.</summary>
    private static byte[] Pixels(byte[] png, int size)
    {
        using var stream = new MemoryStream(png);
        using var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);

        var pixels = new byte[size * size * 4];
        nint scratch = System.Runtime.InteropServices.Marshal.AllocHGlobal(pixels.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size, size), scratch, pixels.Length, size * 4);
            System.Runtime.InteropServices.Marshal.Copy(scratch, pixels, 0, pixels.Length);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(scratch);
        }

        return pixels;
    }

    /// <summary>The bounding box of every non-transparent pixel.</summary>
    private static Rect InkBounds(byte[] png, int size)
    {
        byte[] pixels = Pixels(png, size);

        int minX = size, minY = size, maxX = -1, maxY = -1;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (pixels[((y * size) + x) * 4 + 3] > 8)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        maxX.Should().BeGreaterThanOrEqualTo(0, "the icon must not be blank");
        return new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// A Windows .ico whose entries are PNGs.
    /// </summary>
    /// <remarks>
    /// Every Windows since Vista reads PNG-compressed entries, and they are the
    /// only sane way to carry 256px: the BMP form would need an AND mask and would
    /// be several times the size for the same picture.
    /// </remarks>
    private static byte[] BuildIco()
    {
        byte[][] images = [.. IcoSizes.Select(size => ClaudeMark.ToPng(size, Coral, Inset))];

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);                  // reserved
        writer.Write((ushort)1);                  // type: icon
        writer.Write((ushort)images.Length);

        int offset = 6 + (16 * images.Length);
        for (int i = 0; i < images.Length; i++)
        {
            int size = IcoSizes[i];

            // 256 is written as 0: the field is one byte and 256 does not fit.
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);                // palette colours
            writer.Write((byte)0);                // reserved
            writer.Write((ushort)1);              // colour planes
            writer.Write((ushort)32);             // bits per pixel
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }

        foreach (byte[] image in images)
        {
            writer.Write(image);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// A macOS .icns: 'icns', a total length, then one length-prefixed PNG per type.
    /// </summary>
    /// <remarks>Every length in this container is big-endian, including the header's.</remarks>
    private static byte[] BuildIcns()
    {
        using var body = new MemoryStream();
        byte[] length = new byte[4];
        foreach ((string type, int pixels) in IcnsEntries)
        {
            byte[] png = ClaudeMark.ToPng(pixels, Coral, Inset);

            body.Write(System.Text.Encoding.ASCII.GetBytes(type));

            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)(png.Length + 8));
            body.Write(length);

            body.Write(png);
        }

        byte[] payload = body.ToArray();

        using var stream = new MemoryStream();
        stream.Write(System.Text.Encoding.ASCII.GetBytes("icns"));

        Span<byte> total = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(total, (uint)(payload.Length + 8));
        stream.Write(total);

        stream.Write(payload);
        return stream.ToArray();
    }
}
