using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClaudeStatus.App.Controls;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// Pixel tests for the usage bar.
/// </summary>
/// <remarks>
/// The bar is drawn rather than templated, so nothing but the pixels can say
/// whether it is right: a mistake in <see cref="UsageBar.Render"/> produces a
/// control that lays out perfectly and shows the wrong number. These render it
/// for real and count what came out.
/// </remarks>
[Collection(HeadlessTests.Name)]
[SuppressMessage("Performance", "CA1801:Review unused parameters",
    Justification = "The fixture parameter forces xUnit to start the Avalonia session.")]
public class UsageBarTests(HeadlessAppFixture fixture)
{
    private const int Width = 200;
    private const int Height = 6;

    /// <summary>Renders a bar at <paramref name="percent"/> and counts the filled columns.</summary>
    /// <remarks>
    /// The track and the fill are given fully distinct colours - pure blue and
    /// pure red - so every column can be attributed to one or the other without
    /// depending on the theme, and the antialiasing on the rounded caps cannot
    /// blur the two into a colour that is neither.
    /// </remarks>
    private static int FilledColumns(double percent) => HeadlessAppFixture.Invoke(() =>
    {
        var bar = new UsageBar
        {
            Percent = percent,
            Width = Width,
            Height = Height,
            Track = new SolidColorBrush(Colors.Blue),
            Fill = new SolidColorBrush(Colors.Red),
        };

        bar.Measure(new Size(Width, Height));
        bar.Arrange(new Rect(0, 0, Width, Height));

        using var bitmap = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
        bitmap.Render(bar);

        return CountRedColumns(bitmap);
    });

    /// <summary>Counts the columns whose middle row came out more red than blue.</summary>
    private static int CountRedColumns(RenderTargetBitmap bitmap)
    {
        const int stride = Width * 4;
        byte[] buffer = new byte[stride * Height];

        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(0, 0, Width, Height),
                handle.AddrOfPinnedObject(),
                buffer.Length,
                stride);
        }
        finally
        {
            handle.Free();
        }

        // Skia hands back the platform's native channel order - BGRA on Windows,
        // RGBA on macOS - so the offsets cannot be hard-coded. Reading them the
        // wrong way round swaps the track for the fill, and 50 % still passes
        // because a mirrored bar fills exactly as many columns.
        bool rgba = bitmap.Format == PixelFormat.Rgba8888;
        int redOffset = rgba ? 0 : 2;
        int blueOffset = rgba ? 2 : 0;

        // The middle row, where both the track and the fill are at full height and
        // the semicircular caps are at their widest.
        int middle = Height / 2;
        int count = 0;
        for (int x = 0; x < Width; x++)
        {
            int offset = (middle * stride) + (x * 4);
            byte blue = buffer[offset + blueOffset];
            byte red = buffer[offset + redOffset];
            if (red > blue)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void An_empty_bar_draws_no_fill_at_all()
    {
        // Zero usage and "no reading yet" both arrive here as zero, and neither
        // may come out looking like a small amount of usage.
        FilledColumns(0d).Should().Be(0);
    }

    [Fact]
    public void A_full_bar_reaches_the_far_edge()
    {
        // Two columns of slack for the antialiased right-hand cap.
        FilledColumns(100d).Should().BeGreaterThan(Width - 3);
    }

    [Theory]
    [InlineData(25d)]
    [InlineData(50d)]
    [InlineData(75d)]
    public void The_fill_is_proportional_to_the_reading(double percent)
    {
        int expected = (int)(Width * percent / 100d);

        FilledColumns(percent).Should().BeCloseTo(expected, 2);
    }

    [Fact]
    public void A_tiny_reading_is_still_something_you_can_see()
    {
        fixture.Should().NotBeNull("the fixture is what boots Avalonia for this collection");

        // 1 % of 200px is two pixels, which with rounded caps renders as a lens
        // thinner than the track and reads as empty. The floor is the bar's own
        // height, so a real reading always leaves a visible dot.
        FilledColumns(1d).Should().BeGreaterThanOrEqualTo(Height - 2);
    }

    [Fact]
    public void An_out_of_range_reading_is_clamped_rather_than_overflowing()
    {
        // The endpoint is undocumented; a percentage above 100 or below zero is
        // not something to draw outside the control's own bounds.
        FilledColumns(140d).Should().BeGreaterThan(Width - 3);
        FilledColumns(-20d).Should().Be(0);
    }
}
