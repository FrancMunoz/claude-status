using System.Buffers.Binary;
using ClaudeStatus.App.Tray;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// The macOS menu bar image: the mark or the working dots, with the session box.
/// </summary>
/// <remarks>
/// A pure function, so all of it can be checked without a Mac. The assertions are
/// about the picture - how wide it is, whether it moved, whether it is the same
/// bytes as last time - because "a PNG came back" is what let the tray icon ship
/// invisible on a light taskbar once already.
/// </remarks>
[Collection(HeadlessTests.Name)]
public class MenuBarImageTests(HeadlessAppFixture fixture)
{
    /// <summary>The pixel size in a PNG's IHDR, which is where the platform reads it too.</summary>
    private static (int Width, int Height) SizeOf(byte[] png)
    {
        png.Take(4).Should().Equal(0x89, (byte)'P', (byte)'N', (byte)'G');

        return (
            (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)),
            (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));
    }

    private static byte[] Render(int? frame, int working, int open, bool watching)
        => HeadlessAppFixture.Invoke(() => MenuBarImageRenderer.Render(frame, working, open, watching));

    [Fact]
    public void With_the_watch_off_the_image_is_the_mark_and_little_else()
    {
        // The only thing beside the mark is the sliver that finishes the gap the
        // row's own left side bearing starts - see MenuBarImageRenderer.Gap. A
        // badge would be several times that.
        (int width, int height) = SizeOf(Render(null, 0, 0, watching: false));

        height.Should().Be(MenuBarImageRenderer.Height);
        width.Should().BeGreaterThanOrEqualTo(MenuBarImageRenderer.Height);
        width.Should().BeLessThan(
            MenuBarImageRenderer.Height * 3 / 2, "no box has been drawn");
    }

    [Fact]
    public void The_badge_widens_the_image_rather_than_covering_the_mark()
    {
        // The Windows widget hangs its badge over the mark because a wider widget
        // would shove the whole strip sideways. The menu bar has no such problem
        // and 16 pt is too small to read a count squeezed into half of it.
        (int bare, _) = SizeOf(Render(null, 0, 0, watching: false));
        (int badged, int height) = SizeOf(Render(null, 0, 0, watching: true));

        badged.Should().BeGreaterThan(bare);
        height.Should().Be(MenuBarImageRenderer.Height, "the item's height is the menu bar's");
    }

    [Fact]
    public void The_box_is_one_width_from_nothing_open_to_nine_of_nine()
    {
        // A box that grew with the count would walk the whole item along the menu
        // bar every time a turn started or ended, which is exactly the thing an
        // indicator on the edge of the eye must not do.
        int zero = SizeOf(Render(null, 0, 0, watching: true)).Width;

        SizeOf(Render(null, 9, 9, watching: true)).Width.Should().Be(zero);
        SizeOf(Render(null, 0, 12, watching: true)).Width.Should().Be(zero, "two digits a side are paid for");
    }

    [Fact]
    public void A_third_digit_is_the_one_thing_that_may_widen_it()
    {
        int two = SizeOf(Render(null, 0, 12, watching: true)).Width;

        SizeOf(Render(null, 0, 123, watching: true)).Width.Should().BeGreaterThan(two);
    }

    [Fact]
    public void The_dots_and_the_mark_are_different_pictures()
    {
        byte[] mark = Render(null, 1, 1, watching: true);

        Render(0, 1, 1, watching: true).Should().NotEqual(mark, "a turn in progress replaces the mark");
    }

    [Fact]
    public void Every_frame_of_the_pulse_differs_from_every_other()
    {
        // Six identical frames would be a timer burning CPU to redraw the same
        // image, which is worse than no animation at all.
        var frames = Enumerable
            .Range(0, MenuBarImageRenderer.FrameCount)
            .Select(frame => Convert.ToHexString(Render(frame, 1, 1, watching: true)))
            .ToList();

        frames.Distinct().Should().HaveCount(MenuBarImageRenderer.FrameCount);
    }

    [Fact]
    public void The_pulse_comes_round_to_where_it_started()
    {
        Render(MenuBarImageRenderer.FrameCount, 1, 1, watching: true)
            .Should().BeSameAs(Render(0, 1, 1, watching: true), "one cycle, then again");
    }

    [Fact]
    public void The_same_picture_is_the_same_array_rather_than_a_new_render()
    {
        // What makes a 150 ms timer affordable: the frames are rasterised once and
        // the timer only hands bytes that already exist to the platform.
        Render(2, 1, 3, watching: true).Should().BeSameAs(Render(2, 1, 3, watching: true));
    }

    [Fact]
    public void With_the_watch_off_the_counts_cannot_change_the_image()
    {
        // Nothing is known about sessions while the watch is off, so every count
        // has to collapse to the one bare mark - including through the cache.
        Render(null, 0, 0, watching: false)
            .Should().BeSameAs(Render(null, 4, 7, watching: false));
    }

    [Fact]
    public void The_dots_rest_at_a_third_and_reach_full_strength()
    {
        var first = Enumerable
            .Range(0, MenuBarImageRenderer.FrameCount)
            .Select(frame => MenuBarImageRenderer.DotOpacity(frame, 0))
            .ToList();

        first[0].Should().BeApproximately(0.3d, 1e-9, "the floor, where the wave starts");
        first.Max().Should().BeApproximately(1d, 1e-9);
        first.Min().Should().BeApproximately(0.3d, 1e-9);
    }

    [Fact]
    public void Each_dot_is_two_frames_behind_the_one_before_it()
    {
        // 0.3 s of a 0.9 s cycle cut into six, which is what makes the three read
        // as a wave rather than as one blink in triplicate.
        for (int frame = 0; frame < MenuBarImageRenderer.FrameCount; frame++)
        {
            MenuBarImageRenderer.DotOpacity(frame, 1).Should().BeApproximately(
                MenuBarImageRenderer.DotOpacity(frame - 2, 0), 1e-9);

            MenuBarImageRenderer.DotOpacity(frame, 2).Should().BeApproximately(
                MenuBarImageRenderer.DotOpacity(frame - 4, 0), 1e-9);
        }
    }

    [Fact]
    public void The_three_dots_are_never_lit_alike()
    {
        // One lit and the others on their way is the whole shape of the sign. If
        // two ever matched exactly the row would read as a flash.
        for (int frame = 0; frame < MenuBarImageRenderer.FrameCount; frame++)
        {
            double[] lit =
            [
                MenuBarImageRenderer.DotOpacity(frame, 0),
                MenuBarImageRenderer.DotOpacity(frame, 1),
                MenuBarImageRenderer.DotOpacity(frame, 2),
            ];

            lit.Distinct().Should().HaveCountGreaterThan(1, $"frame {frame}");
        }
    }

    [Fact]
    public void The_fixture_is_what_boots_Avalonia_for_this_collection()
        => fixture.Should().NotBeNull();
}
